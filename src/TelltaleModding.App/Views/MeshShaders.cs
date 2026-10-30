namespace TelltaleModding.App.Views;

/// <summary>
/// The <see cref="MeshViewport"/>'s shaders (GLSL 330 core / 300 es bodies; the version
/// header is prepended at compile time). The scene pass shades in linear light and writes
/// the tone-mapped color, the view-space normal and the color of the surface's outline;
/// the post pass draws ink outlines from the depth and normal targets in that color.
/// </summary>
internal static class MeshShaders
{
    /// <summary>
    /// Skinning in the vertex shader: each vertex blends up to four bones' skinning
    /// matrices (inverse bind × posed world, one row per texel of <c>uBones</c>) by its
    /// weights, divided by the weight sum, as the CPU skinner does. Off (<c>uSkinned</c> 0)
    /// the rest pose draws unchanged.
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
            float total = aWeights.x + aWeights.y + aWeights.z + aWeights.w;
            if (total < 1e-4 || aBones.x < 0.0)
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
        uniform vec3 uSelfLit;         // linear light the surface gives off in its own color
        uniform float uHasTex;
        uniform int uNmapKind;      // -1 none, 0 X in blue / Y in alpha, 1 X in red / Y in green, 2 RGB
        uniform int uSpecLayout;    // 0 none, 1 color = intensity, 2 blue = gloss, alpha = intensity, red = metal
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
        uniform vec4 uLightCone[8];   // cos inner, cos outer, weight relative to the brightest light
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
        uniform float uInkAmbient;    // 1 = graphic black tests all light (a scene's rig), 0 = the key light alone (studio)
        uniform float uInkKeyWrap;    // wrap of that key light (0 = black from the terminator on)
        uniform vec3 uInkFade;        // graphic black fade start, fade end (distance from the eye), line art strength
        uniform vec3 uGrade;          // saturation, contrast
        uniform vec3 uLineColor;      // the outline color: the material's own, otherwise the look's

        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outNormal;
        layout(location = 2) out vec4 outLine;

        // The toon ramp: the level of the band the light falls in, edges blended by the softness.
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

        // Identity up to the shoulder, then a soft roll-off to white: lit textures keep their
        // painted color and only highlights compress.
        vec3 toneMap(vec3 c)
        {
            const float knee = 0.75;
            vec3 over = max(c - knee, 0.0);
            return min(c, vec3(knee)) + (1.0 - knee) * (1.0 - exp(-over / (1.0 - knee)));
        }

        // A sharp threshold at 0.5 with an edge about a pixel wide (the games' sharp-detail filter).
        float sharpInk(float v)
        {
            float w = clamp(fwidth(v) * 1.5, 0.01, 0.5);
            return clamp((v - (0.5 - w)) / (2.0 * w), 0.0, 1.0);
        }

        // The Definitive Series' ink: inked where the filtered texture passes 0.5. The game
        // narrows its sharp-detail edge to a hundredth of a pixel and leaves the stair steps to
        // its anti-aliasing; here the edge is one pixel wide, which keeps the same line width.
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

            float wrap = uLightModel.x;
            vec3 diffuse = vec3(0.0);
            vec3 spec = vec3(0.0);
            vec3 keyLight = vec3(0.0);    // the key light on the smooth surface, for graphic black under the studio rig
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
                    diffuse += c * band(clamp((ndl + wrap) / (1.0 + wrap), 0.0, 1.0) * att);
                if (key)
                    keyLight += c * max((dot(geo, L) + uInkKeyWrap) / (1.0 + uInkKeyWrap), 0.0) * att;   // shadow edges follow the shape, not the bumps
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
                // Baked lighting replaces ambient (and the lights it already holds).
                baked = texture(uLightmap, vUv2).rgb * uLightmapScale;
                ambient = baked;
            }

            if (uInkKind == 2)
            {
                // Line art: ink where the value falls below 0.5.
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
                // Ink texture: white strokes, drawn regardless of light (after the light, before
                // graphic black, as the game does).
                vec4 it = texture(uInkTex, vUv3);
                float a = inkCoverage(mix(it.r, it.a, uInkInAlpha));
                color = mix(color, uInkColor, a * uInk.w);
            }
            if (uInk.x > 0.0)
            {
                // Graphic black: black where the lit color's luminance falls below the
                // threshold, measured before the ink. Under a scene's rig it is the game's test,
                // all light including ambient (baked light always counts). The studio rig's
                // wrapped fill and rim lights leave nothing in shadow, so there the key light
                // alone, unwrapped, stands in for a scene's shadowed key: black falls past the
                // key's terminator, a little sooner on dark materials, as in the game.
                float lum = lightmapped || uInkAmbient > 0.5
                    ? dot(direct + albedo * (lightmapped ? baked : ambient), vec3(0.3, 0.59, 0.11))
                    : dot(albedo * keyLight + spec * specColor, vec3(0.3, 0.59, 0.11));
                float soft = max(uInk.z, 0.001);
                float t = clamp((lum - (uInk.y - 0.5 * soft)) / soft, 0.0, 1.0);
                // Faded in with distance from the eye (the scene's Graphic Black Near / Far).
                float fade = uInkFade.y > uInkFade.x ? clamp((length(uEye - vWorld) - uInkFade.x) / (uInkFade.y - uInkFade.x), 0.0, 1.0) : 1.0;
                color = mix(color, uInkColor, fade * uInk.x * (1.0 - t));
            }
            // Emission glows whatever the light, so graphic black does not cover it.
            if (dot(uEmissiveColor, uEmissiveColor) > 0.0)
                color += pow(texture(uEmissive, vUv).rgb, vec3(2.2)) * uEmissiveColor;
            // A self-lit surface (a hologram) shows its own color at full strength.
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

    /// <summary>
    /// The inverted hull: the mesh pushed out along its normals in clip space, drawn back
    /// faces only in the outline color (the games' SceneToonOutline2 vertex shader).
    /// </summary>
    public const string HullVertex = Skinning + """
        in vec3 aPos;
        in vec3 aNormal;
        uniform mat4 uMvp;
        uniform mat4 uView;
        uniform vec4 uHull;    // width, z min, z max, aspect
        void main()
        {
            const float T = 0.606693;   // tan(31.25°), fixed in the games' shader
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

    /// <summary>Flat-colored lines and points (the skeleton overlay).</summary>
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

        // 1 / view distance (linear in screen space on any plane); 0 for the background.
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
                    // Silhouettes: the pixel stands in front of what its neighbours' average
                    // (on a plane, inverse depth is linear, so slopes cancel out).
                    for (int s = 1; s <= 2; s++)
                    {
                        vec2 o = dirs[k] * r * (0.5 * float(s)) * uTexel;
                        float lap = (ic - 0.5 * (invDepth(vUv + o) + invDepth(vUv - o))) / ic;
                        edge = max(edge, smoothstep(uOutline.y * 0.5, uOutline.y, lap));
                    }
                    // Creases: the surface turns sharply between the two sides.
                    vec2 oc = dirs[k] * max(1.0, r * uOutline.w) * uTexel;
                    vec4 np = texture(uNormal, vUv + oc);
                    vec4 nm = texture(uNormal, vUv - oc);
                    if (np.a > 0.01 && nm.a > 0.01)
                    {
                        float bend = 1.0 - dot(np.xyz * 2.0 - 1.0, nm.xyz * 2.0 - 1.0);
                        edge = max(edge, smoothstep(uOutline.z, uOutline.z + 0.2, bend) * 0.9);
                    }
                }
                // Drawn on the nearer surface, in its color.
                c.rgb = mix(c.rgb, texture(uLine, vUv).rgb, edge * uOutlineOpacity);
            }
            outColor = vec4(c.rgb, 1.0);
        }
        """;
}
