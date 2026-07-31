using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

public sealed class XamlInputScopeContractTests
{
    [Fact]
    public void InputScopeNameValuesMatchOfficialWinUiContract()
    {
        var expected = new (string Name, int Value)[]
        {
            ("Default", 0),
            ("Url", 1),
            ("EmailSmtpAddress", 5),
            ("PersonalFullName", 7),
            ("CurrencyAmountAndSymbol", 20),
            ("CurrencyAmount", 21),
            ("DateMonthNumber", 23),
            ("DateDayNumber", 24),
            ("DateYear", 25),
            ("Digits", 28),
            ("Number", 29),
            ("Password", 31),
            ("TelephoneNumber", 32),
            ("TelephoneCountryCode", 33),
            ("TelephoneAreaCode", 34),
            ("TelephoneLocalNumber", 35),
            ("TimeHour", 37),
            ("TimeMinutesOrSeconds", 38),
            ("NumberFullWidth", 39),
            ("AlphanumericHalfWidth", 40),
            ("AlphanumericFullWidth", 41),
            ("Hiragana", 44),
            ("KatakanaHalfWidth", 45),
            ("KatakanaFullWidth", 46),
            ("Hanja", 47),
            ("HangulHalfWidth", 48),
            ("HangulFullWidth", 49),
            ("Search", 50),
            ("Formula", 51),
            ("SearchIncremental", 52),
            ("ChineseHalfWidth", 53),
            ("ChineseFullWidth", 54),
            ("NativeScript", 55),
            ("Text", 57),
            ("Chat", 58),
            ("NameOrPhoneNumber", 59),
            ("EmailNameOrAddress", 60),
            ("Maps", 62),
            ("NumericPassword", 63),
            ("NumericPin", 64),
            ("AlphanumericPin", 65),
            ("FormulaNumber", 67),
            ("ChatWithoutEmoji", 68)
        };

        var names = Enum.GetNames<InputScopeNameValue>();
        var values = Enum.GetValues<InputScopeNameValue>();
        Assert.Equal(expected.Length, names.Length);
        Assert.Equal(expected.Length, values.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, names[index]);
            Assert.Equal(expected[index].Value, (int)values[index]);
        }
    }

    [Fact]
    public void InputScopeTypesMatchOfficialShapeAndOwnership()
    {
        Assert.True(typeof(InputScope).IsSealed);
        Assert.True(typeof(InputScopeName).IsSealed);
        Assert.Equal(
            typeof(DependencyObject),
            typeof(InputScope).BaseType);
        Assert.Equal(
            typeof(DependencyObject),
            typeof(InputScopeName).BaseType);

        var contentType = typeof(ContentPropertyAttribute);
        var nameField = Assert.Single(
            contentType.GetFields(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(ContentPropertyAttribute.Name),
            nameField.Name);
        Assert.Equal(typeof(string), nameField.FieldType);
        Assert.Empty(
            contentType.GetProperties(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly));
        var usage = Assert.Single(
            contentType.GetCustomAttributes<
                AttributeUsageAttribute>());
        Assert.Equal(AttributeTargets.Class,
            usage.ValidOn);
        Assert.False(usage.AllowMultiple);

        var content = Assert.Single(
            typeof(InputScopeName)
                .GetCustomAttributes<ContentPropertyAttribute>());
        Assert.Equal(nameof(InputScopeName.NameValue), content.Name);
        AssertWinUiContractVersion(typeof(InputScopeNameValue));
        AssertWinUiContractVersion(typeof(InputScopeName));
        AssertWinUiContractVersion(typeof(InputScope));
        AssertWinUiContractVersion(
            typeof(ContentPropertyAttribute));

        var empty = new InputScopeName();
        Assert.Equal(InputScopeNameValue.Default, empty.NameValue);
        var named = new InputScopeName(
            InputScopeNameValue.EmailNameOrAddress);
        Assert.Equal(
            InputScopeNameValue.EmailNameOrAddress,
            named.NameValue);
        named.NameValue = InputScopeNameValue.ChatWithoutEmoji;
        Assert.Equal(
            InputScopeNameValue.ChatWithoutEmoji,
            named.NameValue);

        var scope = new InputScope();
        Assert.Same(scope.Names, scope.Names);
        Assert.Empty(scope.Names);
        scope.Names.Add(named);
        Assert.Same(named, Assert.Single(scope.Names));
    }

    [Fact]
    public void WarmedInputScopeReadsAreAllocationFree()
    {
        const int Count = 1_000_000;
        var scope = new InputScope();
        var name = new InputScopeName(InputScopeNameValue.Number);
        scope.Names.Add(name);
        var checksum = 0;
        for (var index = 0; index < 10_000; index++)
            checksum += (int)scope.Names[0].NameValue;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Count; index++)
            checksum += (int)scope.Names[0].NameValue;
        var after = GC.GetAllocatedBytesForCurrentThread();

        GC.KeepAlive(checksum);
        Assert.Equal(before, after);
    }

    private static void AssertWinUiContractVersion(Type type)
    {
        var attribute = Assert.Single(
            type.GetCustomAttributesData(),
            static candidate =>
                candidate.AttributeType ==
                typeof(ContractVersionAttribute));
        Assert.Equal(
            "Microsoft.UI.Xaml.WinUIContract",
            Assert.IsType<string>(
                attribute.ConstructorArguments[0].Value));
        Assert.Equal(
            0x00010000U,
            Assert.IsType<uint>(
                attribute.ConstructorArguments[1].Value));
    }
}
