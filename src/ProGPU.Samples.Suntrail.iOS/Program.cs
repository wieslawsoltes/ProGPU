using ProGPU.iOS;

ProGPU.Samples.Suntrail.App.AutoPlay = args.Contains("--autoplay", StringComparer.Ordinal);
if (args.Contains("--measure", StringComparer.Ordinal) || Environment.GetEnvironmentVariable("SUNTRAIL_MEASURE") == "1")
{
    ProGPU.Samples.Suntrail.App.AutoPlay = true;
    ProGPU.Samples.Suntrail.App.Started += new ProGPU.Samples.Suntrail.iOS.DeviceMeasurement().Attach;
}
using var feedback = new UIKit.UIImpactFeedbackGenerator(UIKit.UIImpactFeedbackStyle.Light);
ProGPU.Samples.Suntrail.App.TouchFeedback = () => { feedback.ImpactOccurred(.5f); feedback.Prepare(); };
IosApplication.Run<ProGPU.Samples.Suntrail.App>(args, "Suntrail");
