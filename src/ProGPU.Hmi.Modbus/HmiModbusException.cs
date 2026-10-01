namespace ProGPU.Hmi.Modbus;

public sealed class HmiModbusException : IOException
{
    public byte Function { get; }
    public byte ExceptionCode { get; }
    public HmiModbusException(byte function, byte exceptionCode)
        : base($"Modbus function 0x{function:X2} returned exception 0x{exceptionCode:X2}.")
    {
        Function = function; ExceptionCode = exceptionCode;
    }
}
