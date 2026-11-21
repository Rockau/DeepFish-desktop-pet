using System.Windows;

namespace FishPet;

// Right-button gestures adapted from FULASKA's cheek-pinch and tickle behavior.
// Normal clicks still use the configured region response. Only movement claims the gesture.
internal sealed partial class PetWindow
{
    private const string ShoeExpression = "脱鞋";
    private static readonly string[] FootTickleExpressions = ["＞＜", "脸红"];
    private bool IsBarefoot => settings.Outfit.Contains(ShoeExpression);
    private enum ExtraTouch { None, Cheek, Waist, Foot }
    private ExtraTouch extraTouch;
    private bool extraMoved, extraLeftCheek;
    private double extraPull;
    private DateTime extraStarted;
    private bool tickling, extraEffectsActive, footTickleFace;
    private readonly HashSet<string> extraTouchParameters = [];
    private Point footDragOffset;
    private Vector footDragTravel, footDragDirection;
    private DateTime footStepAt;
    private double footTickleStrength, footTicklePhase, footRubTravel;
    private bool footEyesSqueezed;
    private int footSide;

    // Default boxes match the three renderer crops. The feet are below the long skirt's hem.
    private static HitBox ExtraTouchDefault(string shape, string zone) => (shape, zone) switch
    {
        ("chibi", "waist") => new(.35, .555, .30, .095),
        ("chibi", "foot") => new(.34, .785, .32, .11),
        ("full", "waist") => new(.35, .365, .30, .09),
        ("full", "foot") => new(.36, .865, .28, .11),
        (_, "waist") => new(.35, .418, .30, .10),
        _ => new(.36, .875, .28, .122)
    };

    private void BeginExtraTouch(string gesture, Point at, HitBox? rect)
    {
        CancelExtraTouch();
        extraTouch = gesture switch
        {
            "pinch" => ExtraTouch.Cheek,
            "waist" => ExtraTouch.Waist,
            "foot" when IsBarefoot => ExtraTouch.Foot,
            _ => ExtraTouch.None
        };
        if (extraTouch == ExtraTouch.Cheek)
        {
            double middle = rect is { } face ? face.X + face.Width / 2 : .5;
            extraLeftCheek = at.X / Width < middle;
        }
        footSide = at.X / Width < .5 ? 1 : -1;
        footDragOffset = default;
        footDragTravel = footDragDirection = default;
        footTickleStrength = footTicklePhase = footRubTravel = 0;
    }

    private bool MoveExtraTouch(double dx, double dy)
    {
        if (extraTouch == ExtraTouch.None) return false;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (!extraMoved)
        {
            if (distance < 4) return true;
            extraMoved = true;
            StartExtraTouch();
        }
        if (extraTouch == ExtraTouch.Cheek)
            extraPull = Math.Pow(Math.Clamp(distance / Math.Max(30, Width * .32), 0, 1), 1.15);
        else if (extraTouch == ExtraTouch.Foot)
        {
            var offset = new Point(dx, dy);
            var traveled = offset - footDragOffset;
            footDragTravel += traveled;
            footRubTravel += traveled.Length;
            footDragOffset = offset;
        }
        return true;
    }

    private void StartExtraTouch()
    {
        EndDizzy();
        if (afterStroke is not null)
        {
            afterStroke = null;
            afterStrokeMelt = false;
            Face(NoSets, []);
            AimAllZero();
        }
        behavior.Interrupt();
        behavior.Touching = true;
        extraEffectsActive = true;
        YieldMutter();
        extraStarted = DateTime.UtcNow;
        tickling = extraTouch is ExtraTouch.Waist or ExtraTouch.Foot;
        if (extraTouch == ExtraTouch.Foot)
        {
            footStepAt = extraStarted;
            footEyesSqueezed = false;
            Face(NoSets, ["脸红"], 0);
            footTickleFace = true;
        }
        if (tickling)
            Happened("tickle", new { zone = extraTouch == ExtraTouch.Waist ? "waist" : "foot" });
        else
            Happened("pinch_start", new { zone = "face", side = extraLeftCheek ? "left" : "right" });
        StartTouchTimer();
    }

    private bool FinishExtraTouch()
    {
        bool moved = extraMoved;
        bool wasPinching = moved && extraTouch == ExtraTouch.Cheek;
        bool wasTicklingFoot = moved && extraTouch == ExtraTouch.Foot;
        extraTouch = ExtraTouch.None;
        extraMoved = false;
        extraPull = 0;
        if (wasPinching)
            Happened("pinch_end", new { zone = "face" });
        if (wasPinching || wasTicklingFoot)
            StopExtraTouchEffects();
        return moved;
    }

    private void CancelExtraTouch()
    {
        extraTouch = ExtraTouch.None;
        extraMoved = false;
        extraPull = 0;
        StopExtraTouchEffects();
    }

    private void StopExtraTouchEffects()
    {
        if (!extraEffectsActive) return;
        foreach (var parameter in extraTouchParameters)
        {
            if (!springs.TryGetValue(parameter, out var spring)) continue;
            spring.Target = 0;
            spring.K = 180;
            spring.C = 26;
        }
        extraTouchParameters.Clear();
        if (footTickleFace)
        {
            Face(NoSets, []);
            footTickleFace = false;
        }
        footEyesSqueezed = false;
        footDragTravel = footDragDirection = default;
        footRubTravel = 0;
        extraEffectsActive = false;
        tickling = false;
        if (!stroking && !dizzyOn) behavior.Touching = false;
    }

    private void AimExtraTouch(string parameter, double value)
    {
        extraTouchParameters.Add(parameter);
        Aim(parameter, value, 220, 19);
    }

    private void AimFootTouch(string parameter, double value, double stiffness, double damping)
    {
        extraTouchParameters.Add(parameter);
        Aim(parameter, value, stiffness, damping);
    }

    // 脚和重心先跟着手动，肩和头随后反向找平衡。分开带动模型关节，避免整张立绘一起摇。
    private bool StepFootTickle(DateTime now)
    {
        double dt = Math.Clamp((now - footStepAt).TotalSeconds, .004, .05);
        footStepAt = now;
        double scale = Math.Max(1, Width / 340);
        Vector velocity = footDragTravel / (dt * scale);
        double rubbing = Math.Clamp(footRubTravel / (dt * scale * 340), 0, 1);
        double smoothing = 1 - Math.Exp(-dt / (rubbing > footTickleStrength ? .09 : .30));
        footTickleStrength += (rubbing - footTickleStrength) * smoothing;
        var direction = new Vector(Math.Clamp(velocity.X / 420, -1, 1), Math.Clamp(velocity.Y / 420, -1, 1));
        footDragDirection += (direction - footDragDirection) * (1 - Math.Exp(-dt / .11));
        footDragTravel = default;
        footRubTravel = 0;
        var faceStyle = ActionStyleFor("interaction:foot");
        footTicklePhase += dt * (3.8 + footTickleStrength * 7.5) * faceStyle.Speed;

        double strength = footTickleStrength;
        double wave = Math.Sin(footTicklePhase) * strength;
        double delayed = Math.Sin(footTicklePhase - .65) * strength;
        double pullX = Math.Clamp(footDragOffset.X / Math.Max(36, Width * .18), -1, 1);
        double pullY = Math.Clamp(footDragOffset.Y / Math.Max(36, Height * .09), -1, 1);
        double tug = footDragDirection.X * 5.8 + pullX * strength * 3;
        double recoil = (strength * 2.3 + wave * 2.8) * footSide;
        AimFootTouch("post:LEGAngleZ2", recoil - tug * .68, 150, 17);
        AimFootTouch("post:Param33", strength * .26 - footDragDirection.Y * .32, 140, 17);
        AimFootTouch("post:ParamHipAngleZ", tug * .72 - recoil * .55, 128, 16);
        AimFootTouch("post:HipAngleX2", footDragDirection.Y * 4.8 + pullY * strength * 2.3, 118, 15);

        // Aim 会统一乘用户设置的强度，读取上一级的位移时先还原，避免传到下一级又放大。
        double hip = Aimed("post:ParamHipAngleZ") / faceStyle.Strength;
        AimFootTouch("post:ParamBodyAngleZ", -hip * 1.1, 96, 13);
        AimFootTouch("post:ParamBodyAngleX", hip * .42 + footSide * strength * 3.5, 83, 12);
        AimFootTouch("post:ParamBodyAngleX2", strength * (4 + 2 * pullY) + delayed * 1.65, 104, 14);

        double chest = Aimed("post:ParamBodyAngleZ") / faceStyle.Strength;
        double hipPitch = Aimed("post:HipAngleX2") / faceStyle.Strength;
        AimFootTouch("post:ShoulderZ", chest * .7 + delayed * 3.3 * footSide, 81, 11);
        AimFootTouch("post:ShoulderY", strength * 4.5 - hipPitch * .42, 74, 11);
        double armSwing = offendedPose ? .25 : 1;
        AimFootTouch("post:LArm", armSwing * (strength * .2 + chest * .036 + wave * .26 * footSide), 85, 12);
        AimFootTouch("post:RArm", armSwing * (strength * .1 - chest * .03 - delayed * .22 * footSide), 72, 11);
        AimFootTouch("post:LForearm", armSwing * (strength * .27 + delayed * .33 * footSide), 67, 10);
        AimFootTouch("post:RForearm", armSwing * (strength * .15 - wave * .32 * footSide), 76, 11);

        AimFootTouch("FaceAngleZ", -chest * .78 - delayed * 3.8 * footSide, 67, 10);
        AimFootTouch("FaceAngleX", strength * 6.8 * footSide + chest * .32, 74, 11);
        AimFootTouch("FaceAngleY", -strength * (7 + pullY * 4) + delayed * 3.2, 81, 12);
        AimFootTouch("post:LEar1", Math.Sin(footTicklePhase * 1.42) * strength * .18, 85, 11);
        AimFootTouch("post:REar1", -Math.Sin(footTicklePhase * 1.42 + .35) * strength * .18, 75, 10);

        // Exertion selects the eye artwork, while the expression weights and facial springs
        // grade the reaction continuously. A light setting should never force full squeezed eyes.
        double exertion = strength * faceStyle.Strength;
        footEyesSqueezed = exertion > (footEyesSqueezed ? .95 : 1.15);
        double expressionAmount = .05 + strength * .55;
        Face(NoSets, footEyesSqueezed ? FootTickleExpressions : ["脸红"], expressionAmount);
        bool customEyes = faceStyle.Face is "＞＜" or "圈圈眼" or "爱心眼" && expressionAmount * faceStyle.Strength >= .5;
        bool specialEyes = footEyesSqueezed && faceStyle.Face.Length == 0 || customEyes;
        // Ordinary smile/lid parameters would replace the model's special eye artwork.
        AimFootTouch("EyeSmile", specialEyes ? 0 : .15 + strength * .8, 150, 22);
        AimFootTouch("EyeOpen", specialEyes ? 1 : 1 - strength * .88, 150, 22);
        AimFootTouch("BrowInnerUp", strength * .4, 150, 22);
        double laugh = .05 + strength * (.42 + .1 * Math.Sin(footTicklePhase * 1.28));
        AimFootTouch("ParamMouthForm", .12 + strength * .8, 170, 23);
        AimFootTouch("ParamMouthOpenY", laugh, 160, 22);
        AimFootTouch("JawOpen", laugh * .52, 160, 22);
        return true;
    }

    private bool StepExtraTouch(DateTime now)
    {
        if (!extraEffectsActive) return false;
        if (exiting || standby || hiddenForFullscreen || (extraTouch == ExtraTouch.Foot && !IsBarefoot))
        {
            CancelExtraTouch();
            return false;
        }

        double t = (now - extraStarted).TotalSeconds;
        if (extraTouch == ExtraTouch.Cheek && extraMoved)
        {
            double flex = Math.Sin(2 * Math.PI * 3.6 * t) * extraPull * .25;
            AimExtraTouch("CheeckPuff", extraPull);
            AimExtraTouch("post:CheeckPuff2", extraPull * .4);
            // The model names the character's sides; the screen's left is the character's right.
            AimExtraTouch(extraLeftCheek ? "post:CheeckPuffR" : "post:CheeckPuffL", extraPull * .9);
            AimExtraTouch("post:CheeksWiggleX", flex);
            AimExtraTouch(extraLeftCheek ? "post:CheeksWiggleXR" : "post:CheeksWiggleXL", -flex);
            AimExtraTouch("Mouthpucker", extraPull * .32);
            AimExtraTouch("LeanInput", -23 * extraPull);
            AimExtraTouch("post:ParamBodyAngleZ", flex * 5 * Math.Exp(-1.25 * t));
            return true;
        }

        if (!tickling) return false;
        if (extraTouch == ExtraTouch.Foot) return StepFootTickle(now);
        const double duration = 2.6;
        if (t >= duration)
        {
            StopExtraTouchEffects();
            return false;
        }

        double envelope = Math.Min(1, 1.5 * Math.Sin(Math.PI * t / duration));
        double wave = envelope * (.22 + .78 * Math.Exp(-t * 1.3)) * Math.Sin(2 * Math.PI * 2.4 * t);
        double ears = envelope * Math.Sin(2 * Math.PI * 4 * t) * Math.Exp(-.8 * t);
        AimExtraTouch("post:ParamBodyAngleZ", -wave * 7);
        AimExtraTouch("FaceAngleZ", wave * 5);
        AimExtraTouch("post:ParamBodyAngleX2", wave * 6);
        AimExtraTouch("post:ParamHipAngleZ", -wave * 5);
        AimExtraTouch("post:LEGAngleZ2", wave * 8);
        AimExtraTouch("post:LForearm", .16 * envelope + wave * .45);
        AimExtraTouch("post:RForearm", .16 * envelope - wave * .45);
        AimExtraTouch("post:LEar1", ears * .35);
        AimExtraTouch("post:REar1", -ears * .35);
        AimExtraTouch("EyeSmile", envelope * .55);
        AimExtraTouch("ParamMouthForm", envelope * .55);
        return true;
    }
}
