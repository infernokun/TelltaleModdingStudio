using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// What the camera should hold at one moment of a line: the point to look at, the height and
/// width of the region to fit (in the speaker's units), and the yaw (radians, 0 = the front of
/// the rest pose) to look from.
/// </summary>
public readonly record struct SpeakerShot(Vector3 Target, float Height, float Width, float Yaw);

/// <summary>
/// A camera that follows a speaker through a line. It frames the face and shoulders, widens to
/// take in the hands when they come up to gesture, and turns with the chest, so a character
/// who crouches, leans or turns stays in view. The whole line is worked out up front and
/// smoothed both ways in time, so the camera moves ahead of a gesture instead of chasing it.
/// </summary>
public sealed class SpeakerCamera
{
    private const float Step = 1f / 15;
    private const float CenterWindow = 0.5f;   // seconds either side the target and yaw average over
    private const float SizeHold = 0.6f;       // seconds either side a wide moment keeps the shot wide
    private const float SizeWindow = 0.3f;     // then averaged over this, so the zoom eases

    private readonly SpeakerShot[] _shots;

    private SpeakerCamera(SpeakerShot[] shots) => _shots = shots;

    /// <summary>The shot at <paramref name="time"/> seconds into the line.</summary>
    public SpeakerShot At(float time)
    {
        float at = Math.Clamp(time / Step, 0, _shots.Length - 1);
        int i = (int)at;
        if (i >= _shots.Length - 1)
            return _shots[^1];
        float t = at - i;
        var (a, b) = (_shots[i], _shots[i + 1]);
        return new SpeakerShot(Vector3.Lerp(a.Target, b.Target, t), a.Height + (b.Height - a.Height) * t,
            a.Width + (b.Width - a.Width) * t, a.Yaw + (b.Yaw - a.Yaw) * t);
    }

    /// <summary>
    /// Works out the camera for a line <paramref name="duration"/> seconds long, from the
    /// speaker's bone world transforms at any moment (<paramref name="pose"/>). Null when the
    /// skeleton has no head to follow.
    /// </summary>
    public static SpeakerCamera? Build(SkeletonFile skeleton, Func<float, Matrix4x4[]> pose, float duration)
    {
        var rig = Rig.Find(skeleton);
        if (rig is null)
            return null;
        int count = Math.Max(1, (int)MathF.Ceiling(Math.Max(0, duration) / Step) + 1);
        var raw = new SpeakerShot[count];
        for (int i = 0; i < count; i++)
            raw[i] = rig.Value.Shot(pose(i * Step));
        return new SpeakerCamera(Smooth(raw));
    }

    private static SpeakerShot[] Smooth(SpeakerShot[] raw)
    {
        int n = raw.Length;
        // Unwrap the yaw so averaging across ±π does not swing the camera the long way round.
        var yaw = new float[n];
        for (int i = 0; i < n; i++)
        {
            yaw[i] = raw[i].Yaw;
            if (i > 0)
                while (yaw[i] - yaw[i - 1] > MathF.PI) yaw[i] -= 2 * MathF.PI;
            if (i > 0)
                while (yaw[i] - yaw[i - 1] < -MathF.PI) yaw[i] += 2 * MathF.PI;
        }

        int Span(float seconds) => (int)MathF.Round(seconds / Step);
        int c = Span(CenterWindow), h = Span(SizeHold), s = Span(SizeWindow);
        var heldHeight = new float[n];
        var heldWidth = new float[n];
        for (int i = 0; i < n; i++)
        {
            int from = Math.Max(0, i - h), to = Math.Min(n - 1, i + h);
            for (int j = from; j <= to; j++)
            {
                heldHeight[i] = Math.Max(heldHeight[i], raw[j].Height);
                heldWidth[i] = Math.Max(heldWidth[i], raw[j].Width);
            }
        }

        var shots = new SpeakerShot[n];
        for (int i = 0; i < n; i++)
        {
            var target = Vector3.Zero;
            float y = 0;
            int from = Math.Max(0, i - c), to = Math.Min(n - 1, i + c);
            for (int j = from; j <= to; j++)
            {
                target += raw[j].Target;
                y += yaw[j];
            }
            int k = to - from + 1;

            float height = 0, width = 0;
            int sf = Math.Max(0, i - s), st = Math.Min(n - 1, i + s);
            for (int j = sf; j <= st; j++)
            {
                height += heldHeight[j];
                width += heldWidth[j];
            }
            int m = st - sf + 1;
            shots[i] = new SpeakerShot(target / k, height / m, width / m, y / k);
        }
        return shots;
    }

    // The bones the camera watches: the head (with the face hanging off it), the chest the
    // camera turns with, and the arms that can widen the shot.
    private readonly record struct Rig(int Head, int Chest, int[] Face, int[] Arms, Matrix4x4 ChestRest, float Size)
    {
        public static Rig? Find(SkeletonFile skeleton)
        {
            var bones = skeleton.Bones;
            if (bones.Count < 3)
                return null;
            var rest = skeleton.BindPoseWorld();
            float minY = rest.Min(m => m.Translation.Y), maxY = rest.Max(m => m.Translation.Y);
            if (maxY - minY <= 0)
                return null;

            // The head: in the upper part of the body, the bone the most bones hang from
            // directly (the face rig); in a skeleton without one, the highest bone.
            var children = new int[bones.Count];
            foreach (var bone in bones)
                if (bone.ParentIndex >= 0 && bone.ParentIndex < bones.Count)
                    children[bone.ParentIndex]++;
            int head = -1;
            for (int i = 0; i < bones.Count; i++)
                if (rest[i].Translation.Y >= minY + (maxY - minY) * 0.6f && children[i] >= 3 &&
                    (head < 0 || children[i] > children[head]))
                    head = i;
            if (head < 0)
            {
                head = 0;
                for (int i = 1; i < bones.Count; i++)
                    if (rest[i].Translation.Y > rest[head].Translation.Y)
                        head = i;
            }

            int Up(int bone, int levels)
            {
                for (int k = 0; k < levels && bones[bone].ParentIndex >= 0; k++)
                    bone = bones[bone].ParentIndex;
                return bone;
            }
            int neck = Up(head, 1), chest = Up(head, 2);

            bool Under(int bone, int ancestor)
            {
                for (int b = bones[bone].ParentIndex; b >= 0; b = bones[b].ParentIndex)
                    if (b == ancestor)
                        return true;
                return false;
            }
            var face = Enumerable.Range(0, bones.Count).Where(i => Under(i, head)).ToArray();
            var arms = Enumerable.Range(0, bones.Count)
                .Where(i => Under(i, chest) && i != neck && i != head && !Under(i, neck))
                .ToArray();

            float size = rest[head].Translation.Y - minY;
            return size <= 0 ? null : new Rig(head, chest, face, arms, rest[chest], size);
        }

        public SpeakerShot Shot(Matrix4x4[] world)
        {
            // The chest's facing, from how far it has turned from the rest pose (which faces +Z).
            Matrix4x4.Invert(ChestRest, out var restInverse);
            var forward = Vector3.TransformNormal(Vector3.TransformNormal(Vector3.UnitZ, restInverse), world[Chest]);
            float yaw = forward.X * forward.X + forward.Z * forward.Z > 1e-6f ? MathF.Atan2(forward.X, forward.Z) : 0;

            var faceCenter = Face.Length > 0
                ? Face.Aggregate(Vector3.Zero, (sum, i) => sum + world[i].Translation) / Face.Length
                : world[Head].Translation;

            // Head and shoulders, in the frame the camera looks from.
            var (sin, cos) = MathF.SinCos(-yaw);
            Vector2 Flat(Vector3 p)
            {
                var d = p - faceCenter;
                return new Vector2(d.X * cos + d.Z * sin, d.Y);
            }
            float chestY = Flat(world[Chest].Translation).Y;
            float left = -0.09f * Size, right = 0.09f * Size, top = 0.12f * Size;
            float bottom = Math.Min(chestY, -0.18f * Size) - 0.04f * Size;

            // Hands brought up to the chest or face widen the shot to show the gesture.
            foreach (int i in Arms)
            {
                var p = world[i].Translation;
                var f = Flat(p);
                if (f.Y < chestY - 0.08f * Size || Vector3.Distance(p, faceCenter) > 0.7f * Size)
                    continue;
                left = Math.Min(left, f.X - 0.04f * Size);
                right = Math.Max(right, f.X + 0.04f * Size);
                top = Math.Max(top, f.Y + 0.04f * Size);
                bottom = Math.Min(bottom, f.Y - 0.04f * Size);
            }

            // Back from the flat frame to the world: the middle of the region.
            float midX = (left + right) / 2, midY = (top + bottom) / 2;
            var target = faceCenter + new Vector3(midX * cos, midY, midX * sin);
            return new SpeakerShot(target, top - bottom, right - left, yaw);
        }
    }
}
