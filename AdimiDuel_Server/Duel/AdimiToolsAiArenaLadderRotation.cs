using TaleWorlds.Library;
using NumericMatrix = System.Numerics.Matrix4x4;
using NumericQuaternion = System.Numerics.Quaternion;
using NumericVector = System.Numerics.Vector3;

namespace AdimiDuel_Server.Duel;

internal static class AdimiToolsAiArenaLadderRotation
{
    public static MatrixFrame CreateActiveFrame(in MatrixFrame idleWorld)
    {
        NumericQuaternion idleOrientation = GetOrientation(idleWorld);
        const double degreesToRadians = System.Math.PI / 180d;
        Vec3 targetEuler = new(
            (float)(89.43d * degreesToRadians),
            (float)(-7.20d * degreesToRadians),
            (float)(7.08d * degreesToRadians));
        MatrixFrame target = MatrixFrame.Identity;
        target.rotation.ApplyEulerAngles(in targetEuler);
        NumericQuaternion targetOrientation = GetOrientation(target);
        NumericQuaternion delta = NumericQuaternion.Normalize(targetOrientation * NumericQuaternion.Conjugate(idleOrientation));
        return RotateBasis(idleWorld, delta);
    }

    public static MatrixFrame Interpolate(in MatrixFrame idleWorld, in MatrixFrame activeWorld, float progress)
    {
        if (!float.IsFinite(progress))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), "Pose progress must be finite.");
        }

        // Preserve the complete authored endpoint values, including their bits.
        if (progress <= 0f)
        {
            return idleWorld;
        }

        if (progress >= 1f)
        {
            return activeWorld;
        }

        NumericQuaternion idleOrientation = GetOrientation(idleWorld);
        NumericQuaternion activeOrientation = GetOrientation(activeWorld);
        NumericQuaternion delta = NumericQuaternion.Normalize(activeOrientation * NumericQuaternion.Conjugate(idleOrientation));
        NumericQuaternion step = NumericQuaternion.Normalize(NumericQuaternion.Slerp(NumericQuaternion.Identity, delta, progress));
        return RotateBasis(idleWorld, step);
    }

    private static MatrixFrame RotateBasis(in MatrixFrame saved, NumericQuaternion rotation)
    {
        MatrixFrame frame = saved;
        frame.rotation.s = Rotate(saved.rotation.s, rotation);
        frame.rotation.f = Rotate(saved.rotation.f, rotation);
        frame.rotation.u = Rotate(saved.rotation.u, rotation);
        return frame;
    }

    private static Vec3 Rotate(in Vec3 vector, NumericQuaternion rotation)
    {
        NumericVector transformed = NumericVector.Transform(new NumericVector(vector.x, vector.y, vector.z), rotation);
        Vec3 result = vector;
        result.x = transformed.X;
        result.y = transformed.Y;
        result.z = transformed.Z;
        return result;
    }

    private static NumericQuaternion GetOrientation(in MatrixFrame frame)
    {
        if (!IsFinite(frame.origin) || !IsFinite(frame.rotation.s) || !IsFinite(frame.rotation.f) || !IsFinite(frame.rotation.u))
        {
            throw new ArgumentException("The saved pose must be finite.", nameof(frame));
        }

        // Extract only a proper orientation. The full original basis retains
        // its scale, shear and any mirrored handedness when delta is applied.
        double sx = frame.rotation.s.x;
        double sy = frame.rotation.s.y;
        double sz = frame.rotation.s.z;
        double sideLength = System.Math.Sqrt((sx * sx) + (sy * sy) + (sz * sz));
        if (sideLength == 0d)
        {
            throw new ArgumentException("The saved pose has a zero side axis.", nameof(frame));
        }

        sx /= sideLength;
        sy /= sideLength;
        sz /= sideLength;
        double fx = frame.rotation.f.x;
        double fy = frame.rotation.f.y;
        double fz = frame.rotation.f.z;
        double forwardLength = System.Math.Sqrt((fx * fx) + (fy * fy) + (fz * fz));
        double projection = (sx * fx) + (sy * fy) + (sz * fz);
        fx -= sx * projection;
        fy -= sy * projection;
        fz -= sz * projection;
        double perpendicularLength = System.Math.Sqrt((fx * fx) + (fy * fy) + (fz * fz));
        if (perpendicularLength <= forwardLength * 1e-12d)
        {
            throw new ArgumentException("The saved pose has dependent side and forward axes.", nameof(frame));
        }

        fx /= perpendicularLength;
        fy /= perpendicularLength;
        fz /= perpendicularLength;
        double ux = (sy * fz) - (sz * fy);
        double uy = (sz * fx) - (sx * fz);
        double uz = (sx * fy) - (sy * fx);
        double longLength = System.Math.Sqrt(
            ((double)frame.rotation.u.x * frame.rotation.u.x)
            + ((double)frame.rotation.u.y * frame.rotation.u.y)
            + ((double)frame.rotation.u.z * frame.rotation.u.z));
        double normalComponent = (ux * frame.rotation.u.x) + (uy * frame.rotation.u.y) + (uz * frame.rotation.u.z);
        if (System.Math.Abs(normalComponent) <= longLength * 1e-6d)
        {
            throw new ArgumentException("The saved pose has a degenerate basis.", nameof(frame));
        }

        var proper = new NumericMatrix(
            (float)sx, (float)sy, (float)sz, 0f,
            (float)fx, (float)fy, (float)fz, 0f,
            (float)ux, (float)uy, (float)uz, 0f,
            0f, 0f, 0f, 1f);
        return NumericQuaternion.Normalize(NumericQuaternion.CreateFromRotationMatrix(proper));
    }

    private static bool IsFinite(in Vec3 vector)
    {
        return float.IsFinite(vector.x) && float.IsFinite(vector.y) && float.IsFinite(vector.z);
    }
}
