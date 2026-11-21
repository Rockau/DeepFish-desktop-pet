using System.Windows;

namespace FishPet;

internal sealed partial class PetWindow
{
    private const string SkirtExpression = "裙子切换";
    private const double ClothesDragStart = 6;
    private bool clothesArmed, clothesMoving, longSkirtAtPress;
    private double clothesDx, clothesDy;

    private void ArmClothesDrag(string zone)
    {
        clothesArmed = zone == "skirt";
        clothesMoving = false;
        longSkirtAtPress = settings.Outfit.Contains(SkirtExpression);
        clothesDx = clothesDy = 0;
    }

    private bool MoveClothesDrag(double dx, double dy)
    {
        if (!clothesArmed) return false;
        clothesDx = dx;
        clothesDy = dy;
        if (!clothesMoving)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < ClothesDragStart) return true;
            clothesMoving = true;
            EndDizzy();
            if (afterStroke != null)
            {
                afterStroke = null;
                afterStrokeMelt = false;
                Face(NoSets, []);
                AimAllZero();
            }
            YieldMutter();
            behavior.Interrupt();
            behavior.Touching = true;
            StartTouchTimer();
        }
        // These offsets are applied after physics, which otherwise overwrites the skirt and torso's final pose.
        double turn = Math.Clamp(dx / Math.Max(18, Width * .15), -1, 1);
        double pull = Math.Clamp(dy / SkirtPullDistance(Height), -1, 1);
        Aim("post:ParamBodyAngleZ", 7.2 * turn, 145, 16);
        Aim("post:ParamHipAngleZ", -5.12 * turn, 145, 16);
        Aim("post:ParamBodyAngleY", 4.8 * turn, 145, 16);
        Aim("post:SkirtX1", .32 * turn, 145, 16);
        Aim("post:SkirtX2", .23 * turn, 128, 14);
        Aim("post:SkirtY", -.28 * pull, 145, 16);
        return true;
    }

    private bool FinishClothesDrag()
    {
        if (!clothesArmed) return false;
        bool moved = clothesMoving;
        CancelClothesDrag();
        if (!moved) return false;
        bool? target = SkirtLengthAfterDrag(longSkirtAtPress, clothesDx, clothesDy, Height);
        if (target is { } makeLong && settings.Outfit.Contains(SkirtExpression) == longSkirtAtPress)
            Dress(SkirtExpression, makeLong);
        Happened("clothes_drag", new { zone = "skirt", longSkirt = settings.Outfit.Contains(SkirtExpression) });
        return true;
    }

    internal static bool? SkirtLengthAfterDrag(bool isLong, double dx, double dy, double height)
    {
        if (Math.Abs(dy) < SkirtPullDistance(height) || Math.Abs(dy) < Math.Abs(dx) * 1.35) return null;
        if (isLong && dy < 0) return false;
        if (!isLong && dy > 0) return true;
        return null;
    }

    private static double SkirtPullDistance(double height) => Math.Clamp(height * .045, 20, 48);

    private void CancelClothesDrag()
    {
        clothesArmed = false;
        if (!clothesMoving) return;
        clothesMoving = false;
        Aim("post:ParamBodyAngleZ", 0, 105, 10);
        Aim("post:ParamHipAngleZ", 0, 105, 10);
        Aim("post:ParamBodyAngleY", 0, 105, 10);
        Aim("post:SkirtX1", 0, 105, 10);
        Aim("post:SkirtX2", 0, 105, 10);
        Aim("post:SkirtY", 0, 105, 10);
        behavior.Touching = false;
        StartTouchTimer();
    }
}
