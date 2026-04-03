// BGRA→RGBA conversion + Y-flip compute shader
// Reads BGRA source texture, writes RGBA to destination with vertical flip.
// Dispatched as ceil(width/16) x ceil(height/16) thread groups.

Texture2D<float4> Source : register(t0);     // BGRA as float4 (hardware swizzles to RGBA on read)
RWTexture2D<uint> Dest : register(u0);       // RGBA packed as R32_UINT

cbuffer Constants : register(b0)
{
    uint Width;
    uint Height;
};

[numthreads(16, 16, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;

    // When B8G8R8A8_UNORM is read as float4 via SRV, GPU auto-swizzles to RGBA order
    float4 color = Source[id.xy];

    // Pack as RGBA uint: R in byte0, G in byte1, B in byte2, A=0xFF in byte3
    uint r = (uint)(saturate(color.r) * 255.0);
    uint g = (uint)(saturate(color.g) * 255.0);
    uint b = (uint)(saturate(color.b) * 255.0);
    uint rgba = r | (g << 8) | (b << 16) | 0xFF000000;

    // Y-flip: write to (x, Height-1-y)
    uint2 dstPos = uint2(id.x, Height - 1 - id.y);
    Dest[dstPos] = rgba;
}
