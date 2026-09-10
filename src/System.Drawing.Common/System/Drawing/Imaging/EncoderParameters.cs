namespace System.Drawing.Imaging;

public sealed class EncoderParameters : IDisposable
{
    public EncoderParameters(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Param = new EncoderParameter[count];
    }

    public EncoderParameters() => Param = new EncoderParameter[1];

    public EncoderParameter[] Param { get; set; }

    public void Dispose()
    {
        if (Param is null)
        {
            return;
        }

        foreach (EncoderParameter? parameter in Param)
        {
            parameter?.Dispose();
        }

        Param = null!;
    }
}
