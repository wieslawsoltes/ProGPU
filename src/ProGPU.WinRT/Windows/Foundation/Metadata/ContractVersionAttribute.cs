namespace Windows.Foundation.Metadata;

[AttributeUsage(
    AttributeTargets.Class |
    AttributeTargets.Delegate |
    AttributeTargets.Enum |
    AttributeTargets.Event |
    AttributeTargets.Field |
    AttributeTargets.Interface |
    AttributeTargets.Method |
    AttributeTargets.Property |
    AttributeTargets.Struct,
    AllowMultiple = true,
    Inherited = false)]
public sealed class ContractVersionAttribute : Attribute
{
    public ContractVersionAttribute(uint version)
    {
    }

    public ContractVersionAttribute(string contract, uint version)
    {
    }

    public ContractVersionAttribute(Type contract, uint version)
    {
    }
}
