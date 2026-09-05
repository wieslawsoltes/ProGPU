using ProGPU.iOS;

ProGPU.Samples.Suntrail.App.AutoPlay = args.Contains("--autoplay", StringComparer.Ordinal);
IosApplication.Run<ProGPU.Samples.Suntrail.App>(args, "Suntrail");
