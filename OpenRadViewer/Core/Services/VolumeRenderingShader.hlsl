cbuffer VrConstants : register(b0)
{
    float4x4 InvViewProj;
    float4   CameraPos;     // xyz = ray origin, w = zoom
    float4   LightDir;      // xyz = light direction, w = stepSize
    float4   WindowParams;  // x = minHU, y = maxHU, z = invWindowWidth, w = preset (0:Bones, 1:Angio, 2:Skin, 3:MIP, 4:Airways)
    float4   VolumeBounds;  // xyz = volume size mm (X, Y, Z), w = unused
    float4   VolumeDims;    // xyz = cols, rows, slices, w = unused
};

Texture3D<float> VolumeTexture : register(t0);
SamplerState     VolumeSampler : register(s0);

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

// Fullscreen triangle vertex shader
VS_OUTPUT VSMain(uint id : SV_VertexID)
{
    VS_OUTPUT output;
    output.TexCoord = float2((id == 2) ? 2.0 : 0.0, (id == 1) ? 2.0 : 0.0);
    output.Position = float4(output.TexCoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

// Ray-AABB intersection
bool IntersectBox(float3 rayOrigin, float3 rayDir, float3 boxMin, float3 boxMax, out float tNear, out float tFar)
{
    float3 invDir = 1.0 / (abs(rayDir) > 1e-6 ? rayDir : float3(1e-6, 1e-6, 1e-6));
    float3 t0 = (boxMin - rayOrigin) * invDir;
    float3 t1 = (boxMax - rayOrigin) * invDir;
    float3 tSmall = min(t0, t1);
    float3 tBig   = max(t0, t1);
    
    tNear = max(max(tSmall.x, tSmall.y), tSmall.z);
    tFar  = min(min(tBig.x, tBig.y), tBig.z);
    
    return (tFar >= max(tNear, 0.0));
}

// Transfer function for 3D Volume
void SampleTransferFunction(float hu, int preset, float minHU, float invWW, out float3 color, out float alpha)
{
    color = float3(0, 0, 0);
    alpha = 0.0;
    
    if (hu < minHU) return;
    
    float u = saturate((hu - minHU) * invWW);
    
    if (preset == 0) // Bones (RadiAnt Golden/Ivory CT)
    {
        alpha = pow(u, 1.4) * 0.95;
        if (u < 0.35)
        {
            float t = u / 0.35;
            color = lerp(float3(0.86, 0.62, 0.40), float3(0.94, 0.78, 0.58), t);
        }
        else if (u < 0.75)
        {
            float t = (u - 0.35) / 0.40;
            color = lerp(float3(0.94, 0.78, 0.58), float3(0.99, 0.92, 0.80), t);
        }
        else
        {
            float t = (u - 0.75) / 0.25;
            color = lerp(float3(0.99, 0.92, 0.80), float3(1.0, 0.98, 0.94), t);
        }
    }
    else if (preset == 1) // Angio (Glowing blood vessels + skeletal background)
    {
        alpha = pow(u, 1.2) * 0.95;
        if (u < 0.20)
        {
            float t = u / 0.20;
            color = lerp(float3(0.65, 0.08, 0.06), float3(0.90, 0.16, 0.10), t);
        }
        else if (u < 0.55)
        {
            float t = (u - 0.20) / 0.35;
            color = lerp(float3(0.90, 0.16, 0.10), float3(0.98, 0.68, 0.15), t);
        }
        else
        {
            float t = (u - 0.55) / 0.45;
            color = lerp(float3(0.98, 0.68, 0.15), float3(0.98, 0.92, 0.80), t);
        }
    }
    else if (preset == 2) // Skin & Scars
    {
        alpha = pow(u, 1.3) * 0.90;
        if (u < 0.40)
        {
            float t = u / 0.40;
            color = lerp(float3(0.85, 0.60, 0.48), float3(0.92, 0.72, 0.58), t);
        }
        else
        {
            float t = (u - 0.40) / 0.60;
            color = lerp(float3(0.92, 0.72, 0.58), float3(0.98, 0.88, 0.80), t);
        }
    }
    else if (preset == 4) // Airways / Lungs
    {
        alpha = pow(u, 1.2) * 0.85;
        color = lerp(float3(0.15, 0.65, 0.95), float3(0.50, 0.90, 0.95), u);
    }
}

// Compute normal using gradient on 3D texture
float3 ComputeNormal(float3 uvw, float3 voxelStep)
{
    float gx = VolumeTexture.SampleLevel(VolumeSampler, uvw + float3(voxelStep.x, 0, 0), 0) -
               VolumeTexture.SampleLevel(VolumeSampler, uvw - float3(voxelStep.x, 0, 0), 0);
               
    float gy = VolumeTexture.SampleLevel(VolumeSampler, uvw + float3(0, voxelStep.y, 0), 0) -
               VolumeTexture.SampleLevel(VolumeSampler, uvw - float3(0, voxelStep.y, 0), 0);
               
    float gz = VolumeTexture.SampleLevel(VolumeSampler, uvw + float3(0, 0, voxelStep.z), 0) -
               VolumeTexture.SampleLevel(VolumeSampler, uvw - float3(0, 0, voxelStep.z), 0);
               
    float3 g = float3(gx, gy, gz);
    float len = length(g);
    if (len > 1e-4)
    {
        return -g / len; // Outward surface normal
    }
    return float3(0, 0, 1);
}

// Pixel Shader
float4 PSMain(VS_OUTPUT input) : SV_TARGET
{
    // Compute World Ray from Inverse ViewProj
    float2 ndc = input.TexCoord * float2(2.0, -2.0) + float2(-1.0, 1.0);
    float4 nearPoint = mul(float4(ndc, 0.0, 1.0), InvViewProj);
    float4 farPoint  = mul(float4(ndc, 1.0, 1.0), InvViewProj);
    nearPoint /= nearPoint.w;
    farPoint  /= farPoint.w;
    
    float3 rayDir = normalize(farPoint.xyz - nearPoint.xyz);
    float3 rayOrigin = nearPoint.xyz;
    
    float3 boxMin = float3(0, 0, 0);
    float3 boxMax = VolumeBounds.xyz;
    
    float tNear, tFar;
    if (!IntersectBox(rayOrigin, rayDir, boxMin, boxMax, tNear, tFar))
    {
        return float4(0, 0, 0, 1);
    }
    
    tNear = max(tNear, 0.0);
    float stepSize = LightDir.w; // mm step size
    int preset = (int)WindowParams.w;
    float minHU = WindowParams.x;
    float maxHU = WindowParams.y;
    float invWW = WindowParams.z;
    
    float3 voxelStep = 1.0 / VolumeDims.xyz;
    float3 light = normalize(LightDir.xyz);
    float3 view = -rayDir;
    float3 halfVec = normalize(light + view);

    // MIP Mode
    if (preset == 3)
    {
        float maxVal = -2000.0;
        [loop]
        for (float t = tNear; t <= tFar; t += stepSize)
        {
            float3 pos = rayOrigin + rayDir * t;
            float3 uvw = pos / VolumeBounds.xyz;
            float val = VolumeTexture.SampleLevel(VolumeSampler, uvw, 0);
            if (val > maxVal) maxVal = val;
        }
        
        float intensity = saturate((maxVal - minHU) * invWW);
        return float4(intensity, intensity, intensity, 1.0);
    }

    // Direct Volume Rendering (DVR) Front-To-Back raymarching
    float4 acc = float4(0, 0, 0, 0);
    
    [loop]
    for (float t = tNear; t <= tFar; t += stepSize)
    {
        float3 pos = rayOrigin + rayDir * t;
        float3 uvw = pos / VolumeBounds.xyz;
        
        float hu = VolumeTexture.SampleLevel(VolumeSampler, uvw, 0);
        
        float3 sampleColor;
        float sampleAlpha;
        SampleTransferFunction(hu, preset, minHU, invWW, sampleColor, sampleAlpha);
        
        if (sampleAlpha > 0.005)
        {
            // Step size opacity adjustment
            sampleAlpha = 1.0 - pow(1.0 - sampleAlpha, stepSize / 1.0);
            
            // Shading
            float3 normal = ComputeNormal(uvw, voxelStep);
            float nDotL = max(0.0, dot(normal, light));
            float diffuse = 0.28 + 0.72 * nDotL;
            
            float nDotH = max(0.0, dot(normal, halfVec));
            float specular = pow(nDotH, 16.0) * 0.40;
            
            sampleColor = saturate(sampleColor * diffuse + specular);
            
            // Front-to-back blending
            float weight = (1.0 - acc.a) * sampleAlpha;
            acc.rgb += weight * sampleColor;
            acc.a += weight;
            
            if (acc.a >= 0.96) break; // Early ray termination
        }
    }
    
    return float4(acc.rgb, 1.0);
}
