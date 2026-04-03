using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using FrooxEngine.UIX;
using Elements.Core;
using Elements.Assets;
using SkyFrost.Base;
using Key = Renderite.Shared.Key;

namespace DesktopBuddy;

public class DesktopBuddyMod : ResoniteMod
{
    public override string Name => "DesktopBuddy";
    public override string Author => "DesktopBuddy";
    public override string Version => "1.0.0";
    public override string Link => "https://github.com/DesktopBuddy/DesktopBuddy";

    internal static ModConfiguration? Config;

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> FrameRate =
        new("frameRate", "Target capture frame rate", () => 30);

    internal static readonly List<DesktopSession> ActiveSessions = new();
    private static int _nextStreamId;

    // Track our desktop canvases so the locomotion patch can identify them
    internal static readonly HashSet<RefID> DesktopCanvasIds = new();

    // Shared stream registry: multiple panels for the same hwnd share one encoder
    private static readonly Dictionary<IntPtr, SharedStream> _sharedStreams = new();

    private class SharedStream
    {
        public int StreamId;
        public NvEncHlsEncoder Encoder;
        public Uri StreamUrl;
        public int RefCount;
    }

    internal static MjpegServer? StreamServer;
    private const int STREAM_PORT = 48080;
    internal static string? TunnelUrl; // Set by cloudflared if available
    internal static readonly PerfTimer Perf = new();

    public override void OnEngineInit()
    {
        Config = GetConfiguration();
        Config!.Save(true);

        Harmony harmony = new("com.desktopbuddy.mod");
        harmony.PatchAll();

        // Start streaming server for remote user support
        try
        {
            StreamServer = new MjpegServer(STREAM_PORT);
            StreamServer.Start();
            Msg($"Stream server started on port {STREAM_PORT}");
        }
        catch (Exception ex)
        {
            Msg($"Stream server failed to start: {ex.Message}");
            StreamServer = null;
        }

        // Start cloudflared tunnel in background (if available)
        if (StreamServer != null)
        {
            System.Threading.Tasks.Task.Run(() => StartTunnel());
        }

        Msg("DesktopBuddy initialized!");
    }

    internal static void SpawnStreaming(World world, IntPtr hwnd, string title)
    {
        try
        {
            Msg($"[SpawnStreaming] Starting for '{title}' hwnd={hwnd}");
            var localUser = world.LocalUser;
            if (localUser == null) { Msg("[SpawnStreaming] LocalUser is null, aborting"); return; }
            var userRoot = localUser.Root;
            if (userRoot == null) { Msg("[SpawnStreaming] UserRoot is null, aborting"); return; }

            var root = world.RootSlot.AddSlot("Desktop Buddy");

            var headPos = userRoot.HeadPosition;
            var headRot = userRoot.HeadRotation;
            var forward = headRot * float3.Forward;
            root.GlobalPosition = headPos + forward * 0.8f;
            root.GlobalRotation = floatQ.LookRotation(forward, float3.Up);
            Msg($"[SpawnStreaming] Slot created at pos={root.GlobalPosition}");

            StartStreaming(root, hwnd, title);
        }
        catch (Exception ex)
        {
            Msg($"ERROR in SpawnStreaming: {ex}");
        }
    }

    private static void StartStreaming(Slot root, IntPtr hwnd, string title)
    {
        Msg($"[StartStreaming] Window: {title} (hwnd={hwnd})");

        // Restore if minimized before attempting capture
        WindowInput.RestoreIfMinimized(hwnd);

        var streamer = new DesktopStreamer(hwnd);
        if (!streamer.TryInitialCapture())
        {
            Msg($"[StartStreaming] Failed initial capture for: {title}");
            streamer.Dispose();
            return;
        }

        int fps = Config!.GetValue(FrameRate);
        int w = streamer.Width;
        int h = streamer.Height;

        Msg($"[StartStreaming] Window size: {w}x{h}, target {fps}fps");

        // Display slot holds the Canvas — separate from root so keyboard etc. aren't nested inside Canvas
        var displaySlot = root.AddSlot("Display");
        Msg("[StartStreaming] Display slot created");

        // Per-user visibility: preview only visible to the spawner, hidden from others
        var displayVis = displaySlot.AttachComponent<ValueUserOverride<bool>>();
        displayVis.Target.Target = displaySlot.ActiveSelf_Field;
        displayVis.Default.Value = false; // Other users: hidden
        displayVis.CreateOverrideOnWrite.Value = false;
        displayVis.SetOverride(root.World.LocalUser, true); // Spawner: visible
        Msg("[StartStreaming] Per-user visibility set");

        // SolidColorTexture as our procedural texture host
        var texSlot = displaySlot.AddSlot("Texture");
        var procTex = texSlot.AttachComponent<SolidColorTexture>();
        procTex.Size.Value = new int2(w, h);
        procTex.Format.Value = Renderite.Shared.TextureFormat.RGBA32;
        procTex.Mipmaps.Value = false;
        procTex.FilterMode.Value = Renderite.Shared.TextureFilterMode.Bilinear;
        Msg("[StartStreaming] Texture component created");

        // Canvas with RawImage pointing at the texture — on displaySlot, NOT root
        float canvasScale = 0.001f;
        var ui = new UIBuilder(displaySlot, w, h, canvasScale);
        var rawImage = ui.RawImage(procTex);
        Msg("[StartStreaming] Canvas + RawImage created");

        // Opaque material so the texture isn't transparent
        var mat = displaySlot.AttachComponent<UI_UnlitMaterial>();
        mat.BlendMode.Value = BlendMode.Opaque;
        rawImage.Material.Target = mat;

        // Attach Button to the RawImage's slot for touch input
        var btn = rawImage.Slot.AttachComponent<Button>();
        btn.PassThroughHorizontalMovement.Value = false;
        btn.PassThroughVerticalMovement.Value = false;
        Msg("[StartStreaming] Button attached");

        // Create session early so event handlers can reference it
        var session = new DesktopSession
        {
            Streamer = streamer,
            Texture = procTex,
            Canvas = ui.Canvas,
            Root = root,
            TargetInterval = 1.0 / fps,
            Hwnd = hwnd,
        };
        ActiveSessions.Add(session);
        DesktopCanvasIds.Add(ui.Canvas.ReferenceID);
        Msg($"[StartStreaming] Registered canvas {ui.Canvas.ReferenceID} for locomotion suppression");

        // --- Input event handlers ---

        // Helper: check if this source should control the mouse hover
        bool IsActiveSource(Component source)
        {
            if (session.LastActiveSource == null || session.LastActiveSource.IsDestroyed)
                return true;
            return source == session.LastActiveSource;
        }

        void ClaimSource(Component source, string reason)
        {
            if (source != session.LastActiveSource)
            {
                // Source claimed
                session.LastActiveSource = source;
            }
        }

        // Find the InteractionHandler from a button event source.
        // The source is a RelayTouchSource on the "Laser" slot. InteractionLaser._handler
        // points to InteractionHandler but is protected. Instead, get the InteractionLaser
        // on the same slot, then read _handler via reflection.
        var _handlerField = typeof(InteractionLaser)
            .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);

        InteractionHandler FindHandler(Component source)
        {
            if (source == null) return null;
            // The source (RelayTouchSource) is on the Laser slot alongside InteractionLaser
            var laser = source.Slot?.GetComponent<InteractionLaser>();
            if (laser != null && _handlerField != null)
            {
                var handlerRef = _handlerField.GetValue(laser) as SyncRef<InteractionHandler>;
                return handlerRef?.Target;
            }
            // Fallback: walk parents (works for non-laser sources)
            return source.Slot?.GetComponentInParents<InteractionHandler>();
        }

        // Get touch ID from source: left hand=0, right hand=1, fallback=0
        uint GetTouchId(Component source)
        {
            var handler = FindHandler(source);
            if (handler != null && handler.Side.Value == Renderite.Shared.Chirality.Right)
                return 1;
            return 0;
        }

        // Hover enter: focus window only if this is the active source
        btn.LocalHoverEnter += (IButton b, ButtonEventData data) =>
        {
            // HoverEnter
        };

        // Touch down — replaces mouse click for drag-to-scroll, hold-to-right-click, multi-touch
        btn.LocalPressed += (IButton b, ButtonEventData data) =>
        {
            ClaimSource(data.source, "touch");
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            uint touchId = GetTouchId(data.source);
            // Touch down
            WindowInput.FocusWindow(hwnd);
            WindowInput.SendTouchDown(hwnd, u, v, streamer.Width, streamer.Height, touchId);
        };

        // Touch move (drag)
        btn.LocalPressing += (IButton b, ButtonEventData data) =>
        {
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            uint touchId = GetTouchId(data.source);
            WindowInput.SendTouchMove(hwnd, u, v, streamer.Width, streamer.Height, touchId);
        };

        // Touch up (release)
        btn.LocalReleased += (IButton b, ButtonEventData data) =>
        {
            float u = data.normalizedPressPoint.x;
            float v = 1f - data.normalizedPressPoint.y;
            uint touchId = GetTouchId(data.source);
            // Touch up
            WindowInput.SendTouchUp(hwnd, u, v, streamer.Width, streamer.Height, touchId);
        };

        // Hover: move cursor + handle scroll (mouse wheel + VR joystick)
        btn.LocalHoverStay += (IButton b, ButtonEventData data) =>
        {
            float hu = data.normalizedPressPoint.x;
            float hv = 1f - data.normalizedPressPoint.y;

            // Only move mouse for the active source (prevents two VR hands fighting)
            if (IsActiveSource(data.source))
            {
                WindowInput.SendHover(hwnd, hu, hv, streamer.Width, streamer.Height);
            }

            // --- Mouse wheel scroll (desktop mode) ---
            var mouse = root.World.InputInterface.Mouse;
            if (mouse != null)
            {
                float scrollY = mouse.ScrollWheelDelta.Value.y;
                if (scrollY != 0)
                {
                    ClaimSource(data.source, "mouse-scroll");
                    WindowInput.FocusWindow(hwnd);
                    int wheelDelta = scrollY > 0 ? 120 : -120;
                    WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta);
                }
            }

            // --- VR joystick scroll (all controllers) ---
            try
            {
                var handler = FindHandler(data.source);
                if (handler == null && !session.JoystickDiagLogged)
                {
                    session.JoystickDiagLogged = true;
                    // Scroll diag: handler null
                }
                if (handler != null)
                {
                    var side = handler.Side.Value;
                    var controller = root.World.InputInterface.GetControllerNode(side);
                    if (controller == null && !session.JoystickDiagLogged)
                    {
                        session.JoystickDiagLogged = true;
                        // Scroll diag: controller null
                    }
                    if (controller != null)
                    {
                        float axisY = controller.Axis.Value.y;
                        if (!session.JoystickDiagLogged)
                        {
                            session.JoystickDiagLogged = true;
                            // Scroll diag: first read
                        }
                        if (Math.Abs(axisY) > 0.15f)
                        {
                            double tick = root.World.Time.WorldTime;
                            bool sameDir = session.LastScrollSign == 0 || Math.Sign(axisY) == session.LastScrollSign;
                            // Only scroll once per engine tick + suppress jitter direction reversals
                            if (tick != session.LastScrollTick && sameDir)
                            {
                                session.LastScrollTick = tick;
                                session.LastScrollSign = Math.Sign(axisY);
                                ClaimSource(data.source, $"joystick-scroll-{side}");
                                WindowInput.FocusWindow(hwnd);
                                int wheelDelta = (int)(axisY * 120f);
                                WindowInput.SendScroll(hwnd, hu, hv, streamer.Width, streamer.Height, wheelDelta);
                            }
                        }
                        else
                        {
                            session.LastScrollSign = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!session.JoystickDiagLogged)
                {
                    session.JoystickDiagLogged = true;
                    Msg($"[Scroll] Joystick EXCEPTION: {ex}");
                }
            }
        };

        // Button bar below the canvas
        float worldHalfH = (h / 2f) * canvasScale;
        var btnBarSlot = root.AddSlot("ButtonBar");
        btnBarSlot.LocalPosition = new float3(0f, -worldHalfH - 0.03f, 0f);
        btnBarSlot.LocalScale = float3.One * canvasScale;
        var btnBarCanvas = btnBarSlot.AttachComponent<Canvas>();
        btnBarCanvas.Size.Value = new float2(600, 40);
        var btnBarUi = new UIBuilder(btnBarCanvas);
        btnBarUi.HorizontalLayout(4f);
        btnBarUi.Style.FlexibleWidth = 1f;
        btnBarUi.Style.MinHeight = 40f;

        var kbBtn = btnBarUi.Button("Keyboard");
        var testStreamBtn = btnBarUi.Button("Test Stream");
        var resyncBtn = btnBarUi.Button("Resync");
        var anchorBtn = btnBarUi.Button("Anchor");
        Msg($"[StartStreaming] Button bar created at y={btnBarSlot.LocalPosition.y:F4}");

        Slot keyboardSlot = null;
        kbBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Keyboard] Button pressed!");
            if (keyboardSlot != null && !keyboardSlot.IsDestroyed)
            {
                bool show = !keyboardSlot.ActiveSelf;
                Msg($"[Keyboard] Toggling visibility: {keyboardSlot.ActiveSelf} -> {show}");
                keyboardSlot.ActiveSelf = show;
                if (show)
                {
                    // Reset to default position/rotation in case user dragged it
                    keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.09f, -0.08f);
                    keyboardSlot.LocalRotation = floatQ.Euler(30f, 0f, 0f);
                    keyboardSlot.LocalScale = float3.One;
                }
                return;
            }
            Msg("[Keyboard] Spawning virtual keyboard (favorite or fallback)");
            keyboardSlot = root.AddSlot("Virtual Keyboard");
            // Position just below the keyboard button, angled up toward user
            keyboardSlot.LocalPosition = new float3(0f, -worldHalfH - 0.09f, -0.08f);
            keyboardSlot.LocalRotation = floatQ.Euler(30f, 0f, 0f);
            // Do NOT set LocalScale — the cloud keyboard has its own natural size
            // SpawnEntity loads the user's favorited keyboard from cloud, falls back to SimpleVirtualKeyboard
            keyboardSlot.StartTask(async () =>
            {
                try
                {
                    var vk = await keyboardSlot.SpawnEntity<VirtualKeyboard>(
                        FavoriteEntity.Keyboard,
                        (Slot s) =>
                        {
                            Msg("[Keyboard] Using fallback SimpleVirtualKeyboard");
                            s.AttachComponent<SimpleVirtualKeyboard>();
                            return s.GetComponent<VirtualKeyboard>();
                        });
                    Msg($"[Keyboard] Spawned: {vk != null}, slot children: {keyboardSlot.ChildrenCount}, globalScale={keyboardSlot.GlobalScale}");
                }
                catch (Exception ex)
                {
                    Msg($"[Keyboard] ERROR spawning: {ex}");
                }
            });
        };

        // Test Stream button — toggles the stream overlay visibility for the local user
        Slot streamSlotRef = null; // Will be set when stream is created below
        bool streamTestMode = false;
        ValueUserOverride<bool> streamVisRef = null; // Set when stream is created
        VideoTextureProvider videoTexRef = null; // Set when stream is created
        testStreamBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[TestStream] Button pressed");
            if (streamVisRef != null && !streamVisRef.IsDestroyed)
            {
                streamTestMode = !streamTestMode;
                // Toggle: show stream to spawner (and hide preview), or restore normal
                streamVisRef.SetOverride(root.World.LocalUser, streamTestMode);
                var displayVisComp = displaySlot.GetComponent<ValueUserOverride<bool>>();
                if (displayVisComp != null)
                    displayVisComp.SetOverride(root.World.LocalUser, !streamTestMode);
                Msg($"[TestStream] Test mode: {streamTestMode} (stream={streamTestMode}, preview={!streamTestMode})");
            }
            else
            {
                Msg("[TestStream] No stream available");
            }
        };

        // Resync button — reloads the video stream by clearing and re-setting the URL
        resyncBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Resync] Button pressed");
            if (videoTexRef != null && !videoTexRef.IsDestroyed)
            {
                var savedUrl = videoTexRef.URL.Value;
                Msg($"[Resync] Resetting stream URL: {savedUrl}");
                videoTexRef.URL.Value = null;
                // Re-set URL next frame so FreeAsset completes first
                root.World.RunInUpdates(1, () =>
                {
                    if (videoTexRef != null && !videoTexRef.IsDestroyed)
                    {
                        videoTexRef.URL.Value = savedUrl;
                        Msg($"[Resync] URL restored: {savedUrl}");
                    }
                });
            }
            else
            {
                Msg("[Resync] No stream available");
            }
        };

        // Anchor button — parents/unparents the viewer to the local user
        bool isAnchored = false;
        anchorBtn.LocalPressed += (IButton b, ButtonEventData d) =>
        {
            Msg("[Anchor] Button pressed");
            var localUser = root.World.LocalUser;
            if (localUser?.Root == null) return;
            if (!isAnchored)
            {
                // Save world transform, parent to user, restore world transform
                var pos = root.GlobalPosition;
                var rot = root.GlobalRotation;
                root.SetParent(localUser.Root.Slot, keepGlobalTransform: true);
                Msg($"[Anchor] Anchored to user");
                isAnchored = true;
            }
            else
            {
                var pos = root.GlobalPosition;
                var rot = root.GlobalRotation;
                root.SetParent(root.World.RootSlot, keepGlobalTransform: true);
                Msg($"[Anchor] Unanchored to world");
                isAnchored = false;
            }
        };

        // --- Back panel: dark opaque background with centered icon + title ---
        {
            var backSlot = root.AddSlot("BackPanel");
            backSlot.LocalPosition = new float3(0f, 0f, 0.001f);
            backSlot.LocalRotation = floatQ.Euler(0f, 180f, 0f);
            backSlot.LocalScale = float3.One * canvasScale;

            var backCanvas = backSlot.AttachComponent<Canvas>();
            backCanvas.Size.Value = new float2(w, h);
            var backUi = new UIBuilder(backCanvas);

            // Opaque material for the whole back panel
            var backMat = backSlot.AttachComponent<UI_UnlitMaterial>();
            backMat.BlendMode.Value = BlendMode.Opaque;
            backMat.Sidedness.Value = Sidedness.Double;

            // Dark background filling the whole canvas
            var bg = backUi.Image(new colorX(0.08f, 0.08f, 0.1f, 1f));
            bg.Material.Target = backMat;

            // Vertical layout centered in the background
            backUi.NestInto(bg.RectTransform);
            backUi.VerticalLayout(16f);
            backUi.Style.FlexibleWidth = 1f;
            backUi.Style.FlexibleHeight = 1f;

            // Spacer top
            backUi.Spacer(1f);

            // Icon — fixed size square, centered, high-res from exe
            float iconSize = Math.Min(w, h) * 0.25f;
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    var iconData = WindowIconExtractor.GetLargeIconRGBA(hwnd, out int iw, out int ih, 128);
                    if (iconData != null && iw > 0 && ih > 0)
                    {
                        // Fixed height row for the icon
                        backUi.Style.MinHeight = iconSize;
                        backUi.Style.PreferredHeight = iconSize;
                        backUi.Style.FlexibleHeight = -1f;

                        // Icon as RawImage with PreserveAspect — let the layout give it full row width,
                        // PreserveAspect will letterbox it within the fixed-height row
                        var iconTex = backSlot.AttachComponent<StaticTexture2D>();
                        var iconMat = backSlot.AttachComponent<UI_UnlitMaterial>();
                        iconMat.Texture.Target = iconTex;
                        iconMat.OffsetFactor.Value = -1f;
                        // Set texture on BOTH RawImage (for PreserveAspect) and material (for rendering)
                        var iconImg = backUi.RawImage(iconTex);
                        iconImg.PreserveAspect.Value = true;
                        iconImg.Material.Target = iconMat;

                        var capturedIconData = iconData;
                        var capturedIw = iw;
                        var capturedIh = ih;
                        var capturedTex = iconTex;
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                var bitmap = new Bitmap2D(capturedIconData, capturedIw, capturedIh,
                                    Renderite.Shared.TextureFormat.RGBA32, false, Renderite.Shared.ColorProfile.sRGB, false);
                                var uri = await root.Engine.LocalDB.SaveAssetAsync(bitmap).ConfigureAwait(false);
                                if (uri != null)
                                {
                                    capturedTex.World.RunInUpdates(0, () =>
                                    {
                                        if (!capturedTex.IsDestroyed)
                                            capturedTex.URL.Value = uri;
                                    });
                                }
                            }
                            catch (Exception ex) { Msg($"[BackPanel] Icon save error: {ex.Message}"); }
                        });
                        backUi.Style.FlexibleHeight = 1f;
                        Msg("[BackPanel] Icon added");
                    }
                }
                catch (Exception ex) { Msg($"[BackPanel] Icon error: {ex.Message}"); }
            }

            // Title text
            backUi.Style.MinHeight = 64f;
            backUi.Style.PreferredHeight = 64f;
            backUi.Style.FlexibleHeight = -1f;
            var text = backUi.Text(title, bestFit: true, alignment: Alignment.MiddleCenter);
            text.Size.Value = 48f;
            text.Color.Value = new colorX(0.9f, 0.9f, 0.9f, 1f);

            // Fix text z-fighting: find the Canvas's auto-created text material and set OffsetFactor
            root.World.RunInUpdates(2, () =>
            {
                try
                {
                    var autoMat = text.Slot.GetComponentInParents<UI_TextUnlitMaterial>();
                    if (autoMat != null)
                    {
                        autoMat.OffsetFactor.Value = -1f;
                        Msg("[BackPanel] Set OffsetFactor=-1 on auto text material");
                    }
                    else
                    {
                        Msg("[BackPanel] Could not find auto UI_TextUnlitMaterial");
                    }
                }
                catch (Exception ex) { Msg($"[BackPanel] Text material fix error: {ex.Message}"); }
            });

            // Spacer bottom
            backUi.Style.FlexibleHeight = 1f;
            backUi.Spacer(1f);

            Msg($"[BackPanel] Created with title '{title}'");
        }

        // --- Remote stream: WGC frames → FFmpeg → MPEG-TS → CloudFlare tunnel → VideoTextureProvider ---
        // Multiple panels for the same hwnd share one encoder (saves GPU/CPU).
        if (StreamServer != null && TunnelUrl != null)
        {
            try
            {
                // Look up or create shared stream for this hwnd
                // Monitors have hwnd=0 — never share those, each is a different capture
                SharedStream shared;
                lock (_sharedStreams)
                {
                    if (hwnd == IntPtr.Zero || !_sharedStreams.TryGetValue(hwnd, out shared))
                    {
                        int streamId = System.Threading.Interlocked.Increment(ref _nextStreamId);
                        var encoder = StreamServer.CreateEncoder(streamId);
                        var url = new Uri($"{TunnelUrl}/stream/{streamId}");
                        shared = new SharedStream { StreamId = streamId, Encoder = encoder, StreamUrl = url, RefCount = 0 };
                        if (hwnd != IntPtr.Zero)
                            _sharedStreams[hwnd] = shared;
                        Msg($"[RemoteStream] Created new shared stream {streamId} for hwnd={hwnd}");
                    }
                    else
                    {
                        Msg($"[RemoteStream] Reusing shared stream {shared.StreamId} for hwnd={hwnd} (refs={shared.RefCount})");
                    }
                    shared.RefCount++;
                }
                session.StreamId = shared.StreamId;
                var nvEncoder = shared.Encoder;

                // Hook NVENC directly into WGC — encodes on GPU, tiny bitstream piped to FFmpeg for HLS muxing
                // Only the first panel's WGC drives the encoder; additional panels skip encoding
                // (all WGC captures for the same hwnd produce identical frames)
                bool isFirstForHwnd = shared.RefCount == 1;
                if (isFirstForHwnd)
                {
                    session.Streamer.OnGpuFrame = (device, texture, fw, fh) =>
                    {
                        if (!nvEncoder.IsInitialized)
                            nvEncoder.Initialize(device, (uint)fw, (uint)fh);
                        nvEncoder.EncodeFrame(texture, (uint)fw, (uint)fh);
                    };
                    Msg($"[RemoteStream] This panel drives the encoder for stream {shared.StreamId}");
                }
                else
                {
                    Msg($"[RemoteStream] This panel shares encoder from stream {shared.StreamId}, no encoding hook");
                }

                // VideoTextureProvider on its own always-active slot (must stay active to load the stream)
                var videoSlot = root.AddSlot("StreamProvider");
                var videoTex = videoSlot.AttachComponent<VideoTextureProvider>();
                videoTex.URL.Value = shared.StreamUrl;
                videoTex.Stream.Value = true;
                videoTex.Volume.Value = 0f;
                videoTexRef = videoTex;

                // Visual display on a separate slot with per-user visibility
                var streamSlot = root.AddSlot("RemoteStreamVisual");
                streamSlot.LocalScale = float3.One * canvasScale;
                streamSlotRef = streamSlot;

                // Per-user visibility on the VISUAL only — VideoTextureProvider stays active
                var streamVis = streamSlot.AttachComponent<ValueUserOverride<bool>>();
                streamVis.Target.Target = streamSlot.ActiveSelf_Field;
                streamVis.Default.Value = true; // Other users: visible
                streamVis.CreateOverrideOnWrite.Value = false;
                streamVis.SetOverride(root.World.LocalUser, false); // Spawner: hidden
                streamVisRef = streamVis;
                Msg("[RemoteStream] Per-user visibility on visual (local=false, others=true)");

                var streamCanvas = streamSlot.AttachComponent<Canvas>();
                streamCanvas.Size.Value = new float2(w, h);
                var streamUi = new UIBuilder(streamCanvas);

                var streamImg = streamUi.RawImage(videoTex);
                var streamMat = streamSlot.AttachComponent<UI_UnlitMaterial>();
                streamMat.BlendMode.Value = BlendMode.Opaque;
                streamImg.Material.Target = streamMat;

                Msg($"[RemoteStream] Created, URL={shared.StreamUrl}, streamId={shared.StreamId}, refs={shared.RefCount}");

                // Monitor state
                int checkCount = 0;
                root.World.RunInUpdates(30, () => CheckVideoState());
                void CheckVideoState()
                {
                    if (videoTex == null || videoTex.IsDestroyed || root.IsDestroyed) return;
                    checkCount++;
                    bool assetAvail = videoTex.IsAssetAvailable;
                    string playbackEngine = videoTex.CurrentPlaybackEngine?.Value ?? "null";
                    bool isPlaying = videoTex.IsPlaying;
                    float clockErr = videoTex.CurrentClockError?.Value ?? -1f;
                    Msg($"[RemoteStream] Check #{checkCount}: avail={assetAvail} engine={playbackEngine} playing={isPlaying} clockErr={clockErr:F2}");
                    if (checkCount < 10)
                        root.World.RunInUpdates(60, () => CheckVideoState());
                    else if (checkCount < 30)
                        root.World.RunInUpdates(60 * 30, () => CheckVideoState());
                }
            }
            catch (Exception ex)
            {
                Msg($"[RemoteStream] ERROR: {ex}");
            }
        }
        else
        {
            Msg($"[RemoteStream] Skipped: StreamServer={StreamServer != null} TunnelUrl={TunnelUrl ?? "null"}");
        }

        // Grabbable with scaling enabled — normalizedPressPoint is 0-1 so input is scale-independent
        var grabbable = root.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = true;
        Msg("[StartStreaming] Grabbable attached");

        root.PersistentSelf = false;
        root.Name = $"Desktop: {title}";

        // Start update loop in this world
        ScheduleUpdate(root.World);

        // Focus the window in Windows immediately so user can start using it
        WindowInput.FocusWindow(hwnd);
        Msg($"[StartStreaming] Window focused, streaming started for: {title}");
    }

    private static readonly HashSet<World> _scheduledWorlds = new();

    internal static void ScheduleUpdate(World world)
    {
        if (_scheduledWorlds.Contains(world)) return;
        _scheduledWorlds.Add(world);
        world.RunInUpdates(1, () => UpdateLoop(world));
    }

    private static int _updateCount;

    // Cached reflection — looked up once, compiled to delegates for zero-overhead per-frame calls
    private static readonly Func<ProceduralTextureBase, Bitmap2D> _getTex2D;
    private static readonly Action<ProceduralTextureBase, Renderite.Shared.TextureUploadHint, FrooxEngine.AssetIntegrated> _setFromBitmap;

    private delegate void SetFromCurrentBitmapDelegate(ProceduralTextureBase instance, Renderite.Shared.TextureUploadHint hint, FrooxEngine.AssetIntegrated callback);

    static DesktopBuddyMod()
    {
        var tex2DGetter = typeof(ProceduralTextureBase)
            .GetProperty("tex2D", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetGetMethod(true);
        if (tex2DGetter != null)
            _getTex2D = (Func<ProceduralTextureBase, Bitmap2D>)Delegate.CreateDelegate(
                typeof(Func<ProceduralTextureBase, Bitmap2D>), tex2DGetter);

        var setMethod = typeof(ProceduralTextureBase)
            .GetMethod("SetFromCurrentBitmap", BindingFlags.NonPublic | BindingFlags.Instance);
        if (setMethod != null)
            _setFromBitmap = (Action<ProceduralTextureBase, Renderite.Shared.TextureUploadHint, FrooxEngine.AssetIntegrated>)
                Delegate.CreateDelegate(
                    typeof(Action<ProceduralTextureBase, Renderite.Shared.TextureUploadHint, FrooxEngine.AssetIntegrated>),
                    setMethod);

        Msg("[DesktopBuddyMod] Compiled reflection delegates: " +
            $"getTex2D={_getTex2D != null}, setFromBitmap={_setFromBitmap != null}");
    }

    private static readonly Stopwatch _perfSw = new();

    private static void CleanupSession(DesktopSession session)
    {
        session.Streamer?.Dispose();
        if (session.Canvas != null) DesktopCanvasIds.Remove(session.Canvas.ReferenceID);

        // Shared stream ref counting — only stop encoder when last panel is removed
        if (session.StreamId > 0)
        {
            lock (_sharedStreams)
            {
                if (_sharedStreams.TryGetValue(session.Hwnd, out var shared) && shared.StreamId == session.StreamId)
                {
                    shared.RefCount--;
                    Msg($"[Cleanup] Stream {shared.StreamId} refs now {shared.RefCount}");
                    if (shared.RefCount <= 0)
                    {
                        _sharedStreams.Remove(session.Hwnd);
                        StreamServer?.StopEncoder(session.StreamId);
                        Msg($"[Cleanup] Last ref removed, encoder {session.StreamId} stopped");
                    }
                }
                else
                {
                    // Orphaned stream (hwnd mismatch or already removed) — stop directly
                    StreamServer?.StopEncoder(session.StreamId);
                    Msg($"[Cleanup] Orphaned stream {session.StreamId} stopped");
                }
            }
        }
    }

    private static void UpdateLoop(World world)
    {
        _updateCount++;
        double dt = world.Time.Delta;

        if (world.IsDestroyed)
        {
            Msg("[UpdateLoop] World destroyed, stopping loop");
            _scheduledWorlds.Remove(world);
            return;
        }

        try
        {
            for (int i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                var session = ActiveSessions[i];

                if (session.Root == null || session.Root.IsDestroyed ||
                    session.Texture == null || session.Texture.IsDestroyed)
                {
                    Msg($"[UpdateLoop] Session {i} root/texture destroyed, cleaning up");
                    CleanupSession(session);
                    ActiveSessions.RemoveAt(i);
                    continue;
                }

                if (session.Root.World != world) continue;
                if (session.UpdateInProgress) continue;

                // Window closed — destroy viewer
                if (!session.Streamer.IsValid)
                {
                    Msg($"[UpdateLoop] Window closed (IsValid=false), destroying viewer");
                    CleanupSession(session);
                    session.Root.Destroy();
                    ActiveSessions.RemoveAt(i);
                    continue;
                }

                // Throttle to target FPS using engine time
                session.TimeSinceLastCapture += dt;
                if (session.TimeSinceLastCapture < session.TargetInterval)
                    continue;
                session.TimeSinceLastCapture = 0;

                // Wait for the asset to be created by the first normal update
                if (!session.Texture.IsAssetAvailable)
                {
                    if (_updateCount <= 5) Msg("[UpdateLoop] Asset not available yet, waiting...");
                    continue;
                }

                // Switch to manual mode after first update so we control the data
                if (!session.ManualModeSet)
                {
                    session.Texture.LocalManualUpdate = true;
                    session.ManualModeSet = true;
                    Msg("[UpdateLoop] Set LocalManualUpdate = true");
                }

                // Get frame
                var frame = session.Streamer.CaptureFrame(out int w, out int h);
                if (frame == null) continue;

                // Window resized — update texture + canvas size, reset manual mode so bitmap gets recreated
                if (session.Texture.Size.Value.x != w || session.Texture.Size.Value.y != h)
                {
                    Msg($"[UpdateLoop] Window resize {session.Texture.Size.Value.x}x{session.Texture.Size.Value.y} -> {w}x{h}");
                    session.Texture.Size.Value = new int2(w, h);
                    if (session.Canvas != null)
                        session.Canvas.Size.Value = new float2(w, h);
                    session.Texture.LocalManualUpdate = false;
                    session.ManualModeSet = false;
                    continue; // Skip this frame, let texture recreate
                }

                var bitmap = _getTex2D?.Invoke(session.Texture);
                if (bitmap == null || bitmap.Size.x != w || bitmap.Size.y != h)
                {
                    if (_updateCount <= 10) Msg($"[UpdateLoop] Bitmap null or size mismatch, waiting...");
                    continue;
                }

                // Copy frame into bitmap
                using (Perf.Time("bitmap_copy"))
                    frame.AsSpan(0, w * h * 4).CopyTo(bitmap.RawData);

                // Upload to GPU
                using (Perf.Time("texture_upload"))
                    _setFromBitmap?.Invoke(session.Texture, default, null);

                Perf.IncrementFrames();
            }
        }
        catch (Exception ex)
        {
            Msg($"ERROR in UpdateLoop: {ex}");
        }

        // Check if any sessions left for this world
        bool hasSessionsInWorld = ActiveSessions.Any(s => s.Root?.World == world);
        if (hasSessionsInWorld)
        {
            world.RunInUpdates(1, () => UpdateLoop(world));
        }
        else
        {
            Msg("[UpdateLoop] No sessions left for this world, stopping loop");
            _scheduledWorlds.Remove(world);
        }
    }

    private static void StartTunnel()
    {
        try
        {
            // Check if cloudflared is available
            var modDir = System.IO.Path.GetDirectoryName(typeof(DesktopBuddyMod).Assembly.Location) ?? "";
            string[] candidates = {
                System.IO.Path.Combine(modDir, "..", "cloudflared", "cloudflared.exe"), // rml_mods/../cloudflared/
                System.IO.Path.Combine(modDir, "cloudflared", "cloudflared.exe"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cloudflared", "cloudflared.exe"),
                @"C:\Program Files (x86)\cloudflared\cloudflared.exe",
                "cloudflared" // PATH
            };
            string cfPath = null;
            foreach (var c in candidates)
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = c, Arguments = "version",
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true
                    });
                    p?.WaitForExit(3000);
                    if (p?.ExitCode == 0) { cfPath = c; break; }
                }
                catch { }
            }

            if (cfPath == null)
            {
                Msg("[Tunnel] cloudflared not found — stream only available on localhost");
                return;
            }

            Msg($"[Tunnel] Starting cloudflared tunnel: {cfPath}");
            var psi = new ProcessStartInfo
            {
                FileName = cfPath,
                // --config NUL bypasses any existing named tunnel config in .cloudflared/config.yml
                Arguments = $"tunnel --config NUL --url http://localhost:{STREAM_PORT}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            if (proc == null) { Msg("[Tunnel] Failed to start cloudflared"); return; }

            // cloudflared prints the tunnel URL to stderr
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Msg($"[Tunnel] {e.Data}");
                // Look for the tunnel URL in output
                if (e.Data.Contains("https://") && e.Data.Contains(".trycloudflare.com"))
                {
                    int idx = e.Data.IndexOf("https://");
                    string url = e.Data.Substring(idx).Trim();
                    int space = url.IndexOf(' ');
                    if (space > 0) url = url.Substring(0, space);
                    // Always strip to origin — cloudflared logs proxied request URLs with paths
                    try { url = new Uri(url).GetLeftPart(UriPartial.Authority); } catch { }
                    TunnelUrl = url;
                    Msg($"[Tunnel] PUBLIC URL: {TunnelUrl}");
                }
            };
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Msg($"[Tunnel] Error: {ex.Message}");
        }
    }

    // Max frame size for pipe throughput: ~4MB/frame works at realtime
    // 1280x720x4 = 3.6MB — tested at 1.8x realtime
    private const int STREAM_MAX_W = 1280;
    private const int STREAM_MAX_H = 720;

    private static void DownscaleAndShareFrame(DesktopSession session, byte[] frame, int w, int h)
    {
        int dstW = w, dstH = h;
        bool needsScale = w > STREAM_MAX_W || h > STREAM_MAX_H;
        if (needsScale)
        {
            float scale = Math.Min((float)STREAM_MAX_W / w, (float)STREAM_MAX_H / h);
            dstW = ((int)(w * scale)) & ~1;
            dstH = ((int)(h * scale)) & ~1;
        }

        lock (session.StreamLock)
        {
            if (!needsScale)
            {
                // Pass through — no copy, FFmpeg handles vflip
                session.StreamFrame = frame;
            }
            else
            {
                // Bilinear-ish downscale (2x2 box average for better quality than nearest-neighbor)
                if (session.ScaledBuffer == null || session.ScaledBuffer.Length != dstW * dstH * 4)
                    session.ScaledBuffer = new byte[dstW * dstH * 4];

                unsafe
                {
                    fixed (byte* srcPtr = frame, dstPtr = session.ScaledBuffer)
                    {
                        uint* src = (uint*)srcPtr;
                        uint* dst = (uint*)dstPtr;
                        for (int y = 0; y < dstH; y++)
                        {
                            int srcY = y * h / dstH;
                            for (int x = 0; x < dstW; x++)
                            {
                                int srcX = x * w / dstW;
                                dst[y * dstW + x] = src[srcY * w + srcX];
                            }
                        }
                    }
                }
                session.StreamFrame = session.ScaledBuffer;
            }
            session.StreamWidth = dstW;
            session.StreamHeight = dstH;
            session.StreamFrameReady = true;
            System.Threading.Monitor.PulseAll(session.StreamLock);
        }
    }

    internal new static void Msg(string msg) => ResoniteMod.Msg(msg);
    internal new static void Error(string msg) => ResoniteMod.Error(msg);
}

/// <summary>
/// Suppress locomotion for the specific hand pointing at a desktop viewer canvas.
/// Patches BeforeInputUpdate (which runs before the input system evaluates bindings).
/// Sets _inputs.Axis.RegisterBlocks = true so the input system blocks the locomotion
/// module from reading this hand's joystick. Only the pointing hand is affected.
///
/// Verified from decompiled source:
/// - InteractionHandler._inputs is private InteractionHandlerInputs (line 206707)
/// - InteractionHandlerInputs.Axis is public readonly Analog2DAction (line 205096)
/// - InputAction.RegisterBlocks is public bool (line 350410)
/// - BeforeInputUpdate normally sets: _inputs.Axis.RegisterBlocks = ActiveTool?.UsesSecondary ?? false (line 207475)
/// - Our postfix overrides that to true when laser touches our canvas
/// </summary>
[HarmonyPatch(typeof(InteractionHandler), nameof(InteractionHandler.BeforeInputUpdate))]
static class LocomotionSuppressionPatch
{
    // InteractionHandler._inputs (private)
    private static readonly FieldInfo _inputsField = typeof(InteractionHandler)
        .GetField("_inputs", BindingFlags.NonPublic | BindingFlags.Instance);
    // InteractionHandlerInputs.Axis (public)
    private static readonly FieldInfo _axisField = typeof(InteractionHandlerInputs)
        .GetField("Axis");

    static void Postfix(InteractionHandler __instance)
    {
        try
        {
            var touchable = __instance.Laser?.CurrentTouchable;
            if (touchable == null) return;

            if (touchable is Canvas canvas && DesktopBuddyMod.DesktopCanvasIds.Contains(canvas.ReferenceID))
            {
                // Set RegisterBlocks = true on this hand's Axis action.
                // This tells the input system that this action is consuming the physical joystick,
                // preventing SmoothLocomotionInputs.Move from reading it.
                if (_inputsField != null && _axisField != null)
                {
                    var inputs = _inputsField.GetValue(__instance);
                    if (inputs is InteractionHandlerInputs typedInputs)
                    {
                        typedInputs.Axis.RegisterBlocks = true;
                    }
                }
            }
        }
        catch
        {
            // Silent — runs every frame
        }
    }
}

/// <summary>
/// Map Resonite Key enum to Windows Virtual Key codes.
/// Verified: Key enum in Renderite.Shared (line 1650)
/// </summary>
static class KeyMapper
{
    public static readonly Dictionary<Key, ushort> KeyToVK = new()
    {
        { Key.Backspace, 0x08 }, { Key.Tab, 0x09 }, { Key.Return, 0x0D },
        { Key.Escape, 0x1B }, { Key.Space, 0x20 }, { Key.Delete, 0x2E },
        { Key.UpArrow, 0x26 }, { Key.DownArrow, 0x28 },
        { Key.LeftArrow, 0x25 }, { Key.RightArrow, 0x27 },
        { Key.Home, 0x24 }, { Key.End, 0x23 },
        { Key.PageUp, 0x21 }, { Key.PageDown, 0x22 },
        { Key.LeftShift, 0xA0 }, { Key.RightShift, 0xA1 },
        { Key.LeftControl, 0xA2 }, { Key.RightControl, 0xA3 },
        { Key.LeftAlt, 0xA4 }, { Key.RightAlt, 0xA5 },
        { Key.LeftWindows, 0x5B }, { Key.RightWindows, 0x5C },
        { Key.F1, 0x70 }, { Key.F2, 0x71 }, { Key.F3, 0x72 }, { Key.F4, 0x73 },
        { Key.F5, 0x74 }, { Key.F6, 0x75 }, { Key.F7, 0x76 }, { Key.F8, 0x77 },
        { Key.F9, 0x78 }, { Key.F10, 0x79 }, { Key.F11, 0x7A }, { Key.F12, 0x7B },
    };

    public static bool IsModifier(Key key) =>
        key == Key.LeftShift || key == Key.RightShift ||
        key == Key.LeftControl || key == Key.RightControl ||
        key == Key.LeftAlt || key == Key.RightAlt;
}

/// <summary>
/// Intercept InputInterface.SimulatePress to forward keys to Windows AND block Resonite.
/// SimulatePress is called for every key press from the virtual keyboard.
/// Modifiers (Shift/Ctrl/Alt) are held down until released by ShiftActive changing.
/// Non-modifier keys get a press+release.
///
/// Verified: SimulatePress(Key key, World origin) at line 359293
/// </summary>
[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.SimulatePress))]
static class SimulatePressPatch
{
    static bool Prefix(Key key, World origin)
    {
        if (DesktopBuddyMod.ActiveSessions.Count == 0 ||
            !DesktopBuddyMod.ActiveSessions.Any(s => s.Root?.World == origin))
        {
            return true; // Not our world, let Resonite handle it
        }

        // Forward to Windows
        if (KeyMapper.KeyToVK.TryGetValue(key, out ushort vk))
        {
            if (KeyMapper.IsModifier(key))
            {
                // Modifier: hold down (don't release — will be released when shift state changes)
                WindowInput.SendVirtualKeyDown(vk);
                // Modifier down
            }
            else
            {
                // Regular key: press and release
                WindowInput.SendVirtualKey(vk);
                // Key press
                // Release any held modifiers after the key press
                WindowInput.ReleaseAllModifiers();
            }
        }
        else
        {
            // Unmapped key ignored
        }

        return false; // Block Resonite
    }
}

/// <summary>
/// Intercept InputInterface.TypeAppend to forward text to Windows AND block Resonite.
/// TypeAppend is called for character input from the virtual keyboard.
///
/// Verified: TypeAppend(string typeDelta, World origin) at line 359277
/// </summary>
[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.TypeAppend))]
static class TypeAppendPatch
{
    static bool Prefix(string typeDelta, World origin)
    {
        if (DesktopBuddyMod.ActiveSessions.Count == 0 ||
            !DesktopBuddyMod.ActiveSessions.Any(s => s.Root?.World == origin))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(typeDelta))
        {
            WindowInput.SendString(typeDelta);
            // Release modifiers after text input
            WindowInput.ReleaseAllModifiers();
        }

        return false;
    }
}

public class DesktopSession
{
    public DesktopStreamer Streamer;
    public SolidColorTexture Texture;
    public Canvas Canvas;
    public Slot Root;
    public bool UpdateInProgress;
    public bool ManualModeSet;
    public double TimeSinceLastCapture;
    public double TargetInterval;

    // VR hand tracking: which source last clicked/scrolled — only this source moves the mouse
    public Component LastActiveSource;

    // Scroll: direction tracking to suppress jitter, tick tracking to prevent burst
    public int LastScrollSign;
    public double LastScrollTick;

    // One-shot diagnostic flag for joystick detection
    public bool JoystickDiagLogged;

    // Unique stream ID (never reused, never shifts)
    public int StreamId;
    public IntPtr Hwnd; // For shared stream cleanup

    // Downscaled frame buffer for stream encoding
    public byte[] ScaledBuffer;

    // Stream: shared frame for FFmpeg encoding (set by update loop, read by encoder thread)
    public volatile byte[] StreamFrame;
    public int StreamWidth, StreamHeight;
    public readonly object StreamLock = new();
    public volatile bool StreamFrameReady;

}
