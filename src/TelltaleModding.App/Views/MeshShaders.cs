namespace TelltaleModding.App.Views;

/// <summary>
/// The <see cref="MeshViewport"/>'s shaders. The version header is added at compile time
/// so the same bodies work for GLSL 330 core and 300 es.
/// </summary>
internal static class MeshShaders
{
    /// <summary>
    /// GPU skinning: blends up to four bone matrices per vertex, normalized by the weight
    /// sum to match the CPU skinner.
    /// </summary>
    public const string Skinning = """
        in vec4 aBones;     // skeleton bone indices, -1 = none
        in vec4 aWeights;
        uniform int uSkinned;
        uniform sampler2D uBones;
        mat4 boneMatrix(int b)
        {
            return mat4(texelFetch(uBones, ivec2(0, b), 0), texelFetch(uBones, ivec2(1, b), 0),
                        texelFetch(uBones, ivec2(2, b), 0), texelFetch(uBones, ivec2(3, b), 0));
        }
        void skin(inout vec3 p, inout vec3 n, inout vec3 t)
        {
            if (uSkinned == 0)
                return;
            // Empty bone slots have zero weight, set by MeshViewport.SetSkinning.
            float total = aWeights.x + aWeights.y + aWeights.z + aWeights.w;
            if (total < 1e-4)
                return;
            vec3 sp = vec3(0.0), sn = vec3(0.0), st = vec3(0.0);
            for (int k = 0; k < 4; k++)
            {
                float w = aWeights[k];
                float b = aBones[k];
                if (w <= 0.0 || b < 0.0)
                    continue;
                mat4 m = boneMatrix(int(b + 0.5));
                sp += (m * vec4(p, 1.0)).xyz * w;
                sn += mat3(m) * n * w;
                st += mat3(m) * t * w;
            }
            p = sp / total;
            if (dot(sn, sn) > 0.0)
                n = normalize(sn);
            if (dot(st, st) > 0.0)
                t = normalize(st);
        }

        """;

    public const string SceneVertex = Skinning + """
        in vec3 aPos;
        in vec3 aNormal;
        in vec2 aUv;
        in vec4 aTangent;
        in vec2 aUv2;
        in vec2 aUv3;
        uniform mat4 uMvp;
        uniform mat4 uView;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vUv;
        out vec4 vTangent;
        out vec3 vViewNormal;
        out float vViewDepth;
        out vec2 vUv2;
        out vec2 vUv3;
        void main()
        {
            vec3 pos = aPos;
            vec3 nrm = aNormal;
            vec3 tan = aTangent.xyz;
            skin(pos, nrm, tan);
            gl_Position = uMvp * vec4(pos, 1.0);
            vWorld = pos;
            vNormal = nrm;
            vUv = aUv;
            vTangent = vec4(tan, aTangent.w);
            vUv2 = aUv2;
            vUv3 = aUv3;
            vViewNormal = mat3(uView) * nrm;
            vViewDepth = -(uView * vec4(pos, 1.0)).z;
        }
        """;

    public const string SceneFragment = """
        in vec3 vWorld;
        in vec3 vNormal;
        in vec2 vUv;
        in vec4 vTangent;
        in vec3 vViewNormal;
        in float vViewDepth;
        in vec2 vUv2;
        in vec2 vUv3;   // the ink texture's UV set

        uniform sampler2D uTex;
        uniform sampler2D uLightmap;
        uniform float uHasLightmap;
        uniform float uLightmapScale;  // stored scale × scene intensity
        uniform sampler2D uNmap;
        uniform sampler2D uSpec;
        uniform sampler2D uInkTex;
        uniform sampler2D uEmissive;
        uniform vec3 uEmissiveColor;   // linear; black = no emissive map
        uniform vec3 uSelfLit;         // self-lit strength, in the surface's own color
        uniform float uHasTex;
        uniform int uNmapKind;      // -1 none, 0 X in blue / Y in alpha, 1 X in red / Y in green, 2 RGB
        uniform int uSpecLayout;    // 0 none, 1 color = intensity, 2 blue = gloss, alpha = intensity, red = metal, 3 color = intensity, alpha = gloss
        uniform int uInkKind;       // 0 none, 1 shadow ink (white = ink), 2 line art (dark = line)
        uniform float uInkInAlpha;
        uniform vec3 uTint;
        uniform vec3 uSpecColor;
        uniform float uSpecPower;
        uniform vec3 uEye;

        uniform int uLightCount;
        uniform vec4 uLightPos[8];    // xyz position, w kind (0 directional, 1 point, 2 spot)
        uniform vec4 uLightDir[8];    // xyz direction of travel, w range (0 = unlimited)
        uniform vec4 uLightColor[8];  // linear color × intensity, w = 1 when it also lights lightmapped surfaces
        uniform vec4 uLightCone[8];   // cos inner, cos outer, weight relative to the brightest light, 1 for a rim light
        uniform vec3 uAmbientSky;
        uniform vec3 uAmbientGround;

        uniform float uExposure;
        uniform vec4 uBands;          // toon ramp thresholds
        uniform vec4 uBandLevels;     // light level of each band
        uniform int uBandCount;       // thresholds in use (0 = smooth light)
        uniform vec3 uBandShape;      // edge softness, smooth light kept, normal-map influence
        uniform vec2 uLightModel;     // diffuse wrap, 1 = only the brightest light is diffuse (toon)
        uniform vec3 uShadowTint;
        uniform vec4 uRim;            // rim strength, rim threshold, specular threshold (0 = smooth), specular strength
        uniform vec4 uInk;            // graphic-black alpha, threshold, softness, ink line strength
        uniform vec3 uInkColor;
        uniform float uInkAmbient;    // 1 = graphic black tests all light, 0 = key light only (studio)
        uniform float uInkKeyWrap;    // wrap of that key light
        uniform vec3 uInkFade;        // graphic black fade start, fade end (distance from the eye), line art strength
        uniform vec3 uGrade;          // saturation, contrast
        uniform vec3 uLineColor;      // outline color

        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outNormal;
        layout(location = 2) out vec4 outLine;

        // Toon ramp.
        float band(float x)
        {
            if (uBandCount == 0)
                return x;
            float v = uBandLevels[0];
            for (int i = 0; i < 3; i++)
            {
                if (i >= uBandCount)
                    break;
                v = mix(v, uBandLevels[i + 1], smoothstep(uBands[i] - uBandShape.x, uBands[i] + uBandShape.x, x));
            }
            return mix(v, x, uBandShape.y);
        }

        // Leaves colors alone up to the knee, then rolls off highlights.
        vec3 toneMap(vec3 c)
        {
            const float knee = 0.75;
            vec3 over = max(c - knee, 0.0);
            return min(c, vec3(knee)) + (1.0 - knee) * (1.0 - exp(-over / (1.0 - knee)));
        }

        // Sharp threshold at 0.5 with a roughly one pixel edge.
        float sharpInk(float v)
        {
            float w = clamp(fwidth(v) * 1.5, 0.01, 0.5);
            return clamp((v - (0.5 - w)) / (2.0 * w), 0.0, 1.0);
        }

        // Definitive Series ink: inked where the texture passes 0.5, with a one pixel edge.
        float inkCoverage(float v)
        {
            return clamp((v - 0.5) / max(fwidth(v), 1e-4) + 0.5, 0.0, 1.0);
        }

        void main()
        {
            vec4 texel = texture(uTex, vUv);
            float alpha = mix(1.0, texel.a, uHasTex);
            if (alpha < 0.04)
                discard;
            vec3 albedo = mix(vec3(0.5), pow(texel.rgb, vec3(2.2)), uHasTex) * uTint;

            vec3 geo = length(vNormal) > 0.1 ? normalize(vNormal) : vec3(0.0, 1.0, 0.0);
            vec3 n = geo;
            if (uNmapKind >= 0 && length(vTangent.xyz) > 0.1)
            {
                vec4 nmapTexel = texture(uNmap, vUv);
                vec3 tsn;
                if (uNmapKind == 2)
                    tsn = nmapTexel.rgb * 2.0 - 1.0;
                else
                {
                    vec2 nxy = (uNmapKind == 0 ? nmapTexel.ba : nmapTexel.rg) * 2.0 - 1.0;
                    tsn = vec3(nxy, sqrt(max(0.0, 1.0 - dot(nxy, nxy))));
                }
                vec3 t = normalize(vTangent.xyz - geo * dot(geo, vTangent.xyz));
                float handed = vTangent.w < 0.0 ? -1.0 : 1.0;
                vec3 b = cross(geo, t) * handed;
                n = normalize(mix(geo, normalize(mat3(t, b, geo) * tsn), uBandShape.z));
            }
            bool lightmapped = uHasLightmap > 0.5;
            vec3 V = normalize(uEye - vWorld);

            vec3 specColor = uSpecColor;
            float power = uSpecPower;
            if (uSpecLayout == 1)
                specColor *= texture(uSpec, vUv).rgb;
            else if (uSpecLayout == 2)
            {
                vec4 sm = texture(uSpec, vUv);
                specColor = mix(specColor * sm.a, sm.a * albedo * 3.0, sm.r) * 0.5;
                power = mix(12.0, 96.0, sm.b);
            }
            else if (uSpecLayout == 3)
            {
                // Roughness is 1 - gloss. Blinn lobe roughly matched to GGX.
                vec4 sm = texture(uSpec, vUv);
                float rough = 1.0 - sm.a;
                float a2 = max(rough * rough * rough * rough, 1e-4);
                power = max(1.0, 2.0 / a2 - 2.0);
                specColor *= sm.rgb * min(2.0, 1.0 / (12.566 * a2));
            }

            float wrap = uLightModel.x;
            vec3 diffuse = vec3(0.0);
            vec3 spec = vec3(0.0);
            vec3 keyLight = vec3(0.0);    // key light only, for studio graphic black
            for (int i = 0; i < 8; i++)
            {
                if (i >= uLightCount)
                    break;
                if (lightmapped && uLightColor[i].w < 0.5)
                    continue;
                vec4 lp = uLightPos[i];
                vec4 ld = uLightDir[i];
                vec3 L;
                float att = 1.0;
                if (lp.w < 0.5)
                    L = -ld.xyz;
                else
                {
                    vec3 d = lp.xyz - vWorld;
                    float dist = max(length(d), 1e-4);
                    L = d / dist;
                    if (ld.w > 0.0)
                    {
                        float x = dist / ld.w;
                        float window = clamp(1.0 - x * x * x * x, 0.0, 1.0);
                        att = window * window / (1.0 + 8.0 * x * x);
                    }
                    if (lp.w > 1.5)
                        att *= smoothstep(uLightCone[i].y, uLightCone[i].x, dot(-L, ld.xyz));
                }
                float ndl = dot(n, L);
                vec3 c = uLightColor[i].rgb;
                bool key = uLightCone[i].z > 0.999;
                if (uLightModel.y < 0.5 || key)
                {
                    // No wrap on rim lights so they stay on the edge.
                    float w = uLightCone[i].w > 0.5 ? 0.0 : wrap;
                    diffuse += c * band(clamp((ndl + w) / (1.0 + w), 0.0, 1.0) * att);
                }
                if (key)
                    keyLight += c * max((dot(geo, L) + uInkKeyWrap) / (1.0 + uInkKeyWrap), 0.0) * att;   // uses the geometry normal, not the normal map
                vec3 H = normalize(L + V);
                float s = pow(max(dot(n, H), 0.0), power) * max(ndl, 0.0) * att;
                if (uRim.z > 0.0)
                    s = smoothstep(uRim.z - 0.05, uRim.z + 0.05, s);
                spec += c * s * uRim.w;
            }
            vec3 ambient = mix(uAmbientGround, uAmbientSky, n.y * 0.5 + 0.5) * uShadowTint;
            vec3 baked = vec3(0.0);
            if (lightmapped)
            {
                // Baked lighting replaces ambient.
                baked = texture(uLightmap, vUv2).rgb * uLightmapScale;
                ambient = baked;
            }

            if (uInkKind == 2)
            {
                // Line art: ink below 0.5.
                vec4 lt = texture(uInkTex, vUv3);
                float paper = sharpInk(mix(lt.r, lt.a, uInkInAlpha));
                albedo *= 1.0 - (1.0 - paper) * uInkFade.z;
            }

            vec3 direct = albedo * diffuse + spec * specColor;
            vec3 color = albedo * ambient + direct;
            float rim = smoothstep(uRim.y, uRim.y + 0.08, 1.0 - max(dot(n, V), 0.0));
            color += rim * uRim.x * (albedo * 1.5 + 0.05);

            if (uInkKind == 1)
            {
                // Ink texture, applied after lighting and before graphic black like the game.
                vec4 it = texture(uInkTex, vUv3);
                float a = inkCoverage(mix(it.r, it.a, uInkInAlpha));
                color = mix(color, uInkColor, a * uInk.w);
            }
            if (uInk.x > 0.0)
            {
                // Graphic black: black where lit luminance is under the threshold. The studio
                // rig leaves nothing in shadow, so there only the key light is tested.
                float lum = lightmapped || uInkAmbient > 0.5
                    ? dot(direct + albedo * (lightmapped ? baked : ambient), vec3(0.3, 0.59, 0.11))
                    : dot(albedo * keyLight + spec * specColor, vec3(0.3, 0.59, 0.11));
                float soft = max(uInk.z, 0.001);
                float t = clamp((lum - (uInk.y - 0.5 * soft)) / soft, 0.0, 1.0);
                // Fade in with distance from the eye.
                float fade = uInkFade.y > uInkFade.x ? clamp((length(uEye - vWorld) - uInkFade.x) / (uInkFade.y - uInkFade.x), 0.0, 1.0) : 1.0;
                color = mix(color, uInkColor, fade * uInk.x * (1.0 - t));
            }
            // Emission goes on top of graphic black.
            if (dot(uEmissiveColor, uEmissiveColor) > 0.0)
                color += pow(texture(uEmissive, vUv).rgb, vec3(2.2)) * uEmissiveColor;
            // Self-lit surfaces show their own color at full strength.
            color += albedo * uSelfLit;

            color = pow(toneMap(color * uExposure), vec3(1.0 / 2.2));
            float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
            color = mix(vec3(luma), color, uGrade.x);
            color = (color - 0.5) * uGrade.y + 0.5;
            outColor = vec4(clamp(color, 0.0, 1.0), alpha);
            vec3 vn = length(vViewNormal) > 0.1 ? normalize(vViewNormal) : vec3(0.0, 0.0, 1.0);
            outNormal = vec4(vn * 0.5 + 0.5, alpha);
            outLine = vec4(uLineColor, alpha);
        }
        """;

    /// <summary>Inverted hull outline: the mesh pushed out along its normals, back faces only.</summary>
    public const string HullVertex = Skinning + """
        in vec3 aPos;
        in vec3 aNormal;
        uniform mat4 uMvp;
        uniform mat4 uView;
        uniform vec4 uHull;    // width, z min, z max, aspect
        void main()
        {
            const float T = 0.606693;   // fixed in the games' shader
            vec3 pos = aPos;
            vec3 nrm = aNormal;
            vec3 unused = vec3(0.0);
            skin(pos, nrm, unused);
            vec4 clip = uMvp * vec4(pos, 1.0);
            float zc = clamp(-(uView * vec4(pos, 1.0)).z, uHull.y, uHull.z);
            vec2 k = max(vec2(T * uHull.w, T) * zc, 0.01);
            vec3 nC = mat3(uMvp) * (length(nrm) > 0.1 ? normalize(nrm) : vec3(0.0, 1.0, 0.0));
            vec2 dir = length(nC.xy) > 1e-5 ? normalize(nC.xy) : vec2(0.0);
            clip.xy += dir * (uHull.x / k) * clip.w;
            clip.z = min(clip.w, clip.z + abs(nC.z) * clip.w * 0.001);
            gl_Position = clip;
        }
        """;

    public const string HullFragment = """
        uniform vec3 uColor;
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outNormal;
        layout(location = 2) out vec4 outLine;
        void main()
        {
            outColor = vec4(uColor, 1.0);
            outNormal = vec4(0.5, 0.5, 1.0, 0.0);
            outLine = vec4(uColor, 1.0);
        }
        """;

    /// <summary>Flat-colored lines and points for the skeleton overlay.</summary>
    public const string LineVertex = """
        in vec3 aPos;
        uniform mat4 uMvp;
        uniform float uPointSize;
        void main()
        {
            gl_Position = uMvp * vec4(aPos, 1.0);
            gl_PointSize = uPointSize;
        }
        """;

    public const string LineFragment = """
        uniform vec4 uColor;
        out vec4 outColor;
        void main()
        {
            outColor = uColor;
        }
        """;

    public const string PostVertex = """
        out vec2 vUv;
        void main()
        {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    public const string PostFragment = """
        in vec2 vUv;
        uniform sampler2D uColor;
        uniform sampler2D uNormal;
        uniform sampler2D uDepth;
        uniform sampler2D uLine;      // each surface's outline color
        uniform vec2 uTexel;
        uniform vec2 uPlanes;         // near, far
        uniform vec4 uOutline;        // width (px), depth threshold, normal threshold, crease width scale
        uniform float uOutlineOpacity;
        uniform vec2 uFalloff;        // distance where lines start thinning (0 = never), thinnest scale
        out vec4 outColor;

        // 1 / view distance, 0 for the background.
        float invDepth(vec2 uv)
        {
            float d = texture(uDepth, uv).r;
            if (d >= 1.0)
                return 0.0;
            float z = d * 2.0 - 1.0;
            float n = uPlanes.x;
            float f = uPlanes.y;
            return (f + n - z * (f - n)) / (2.0 * n * f);
        }

        void main()
        {
            vec4 c = texture(uColor, vUv);
            float width = uOutline.x;
            vec4 nc = texture(uNormal, vUv);
            float ic = invDepth(vUv);
            if (width > 0.0 && nc.a > 0.01 && ic > 0.0)
            {
                float scale = uFalloff.x > 0.0 ? clamp(uFalloff.x * ic, uFalloff.y, 1.0) : 1.0;
                float r = width * scale;
                vec2 dirs[4] = vec2[4](vec2(1.0, 0.0), vec2(0.0, 1.0), vec2(0.7071, 0.7071), vec2(0.7071, -0.7071));
                float edge = 0.0;
                for (int k = 0; k < 4; k++)
                {
                    // Silhouettes: pixel is in front of its neighbors' average.
                    for (int s = 1; s <= 2; s++)
                    {
                        vec2 o = dirs[k] * r * (0.5 * float(s)) * uTexel;
                        float lap = (ic - 0.5 * (invDepth(vUv + o) + invDepth(vUv - o))) / ic;
                        edge = max(edge, smoothstep(uOutline.y * 0.5, uOutline.y, lap));
                    }
                    // Creases: sharp change in normal.
                    vec2 oc = dirs[k] * max(1.0, r * uOutline.w) * uTexel;
                    vec4 np = texture(uNormal, vUv + oc);
                    vec4 nm = texture(uNormal, vUv - oc);
                    if (np.a > 0.01 && nm.a > 0.01)
                    {
                        float bend = 1.0 - dot(np.xyz * 2.0 - 1.0, nm.xyz * 2.0 - 1.0);
                        edge = max(edge, smoothstep(uOutline.z, uOutline.z + 0.2, bend) * 0.9);
                    }
                }
                // Drawn in the nearer surface's outline color.
                c.rgb = mix(c.rgb, texture(uLine, vUv).rgb, edge * uOutlineOpacity);
            }
            outColor = vec4(c.rgb, 1.0);
        }
        """;
}
