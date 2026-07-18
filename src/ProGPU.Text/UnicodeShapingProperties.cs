using System.Globalization;
using System.Text;

namespace ProGPU.Text;

/// <summary>
/// Provides the generated Unicode properties shared by managed and GPU text
/// shaping implementations.
/// </summary>
public static class UnicodeShapingProperties
{
    public enum SyllableMachine : byte { Indic, Use, Myanmar, Khmer }

    public static byte GetArabicJoiningType(uint codePoint) =>
        IsScalar(codePoint) ? (byte)ArabicJoiningData.GetJoiningType(codePoint) : (byte)0;

    public static byte GetCanonicalCombiningClass(uint codePoint) =>
        IsScalar(codePoint) ? UnicodeCombiningClassData.GetCanonicalClass(codePoint) : (byte)0;

    public static ushort GetIndicProperties(uint codePoint) =>
        IsScalar(codePoint) ? IndicShapingData.GetProperties(codePoint) : (ushort)0;

    public static byte GetUseCategory(uint codePoint) =>
        IsScalar(codePoint) ? UseShapingData.GetCategory(codePoint) : (byte)0;

    public static bool IsMark(uint codePoint)
    {
        if (!IsScalar(codePoint)) return false;
        UnicodeCategory category = Rune.GetUnicodeCategory(new Rune(checked((int)codePoint)));
        return category is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    public static uint GetMirroredCodePoint(uint codePoint) =>
        IsScalar(codePoint) ? UnicodeDirectionalData.GetMirroredCodePoint(codePoint) : codePoint;

    public static uint GetVerticalCodePoint(uint codePoint) =>
        IsScalar(codePoint) ? UnicodeDirectionalData.GetVerticalCodePoint(codePoint) : codePoint;

    public static int GetSyllableMachineStateCount(SyllableMachine machine) => machine switch
    {
        SyllableMachine.Indic => IndicSyllableMachineData.StateCount,
        SyllableMachine.Use => UseSyllableMachineData.StateCount,
        SyllableMachine.Myanmar => MyanmarSyllableMachineData.StateCount,
        SyllableMachine.Khmer => KhmerSyllableMachineData.StateCount,
        _ => throw new ArgumentOutOfRangeException(nameof(machine))
    };

    public static int GetSyllableMachineStartState(SyllableMachine machine) => machine switch
    {
        SyllableMachine.Indic => IndicSyllableMachineData.StartState,
        SyllableMachine.Use => UseSyllableMachineData.StartState,
        SyllableMachine.Myanmar => MyanmarSyllableMachineData.StartState,
        SyllableMachine.Khmer => KhmerSyllableMachineData.StartState,
        _ => throw new ArgumentOutOfRangeException(nameof(machine))
    };

    public static (int Target, int Action) GetSyllableTransition(
        SyllableMachine machine, int state, byte category)
    {
        int transition = machine switch
        {
            SyllableMachine.Indic => IndicSyllableMachineData.GetTransition(state, category),
            SyllableMachine.Use => UseSyllableMachineData.GetTransition(state, category),
            SyllableMachine.Myanmar => MyanmarSyllableMachineData.GetTransition(state, category),
            SyllableMachine.Khmer => KhmerSyllableMachineData.GetTransition(state, category),
            _ => throw new ArgumentOutOfRangeException(nameof(machine))
        };
        return GetTransition(machine, transition);
    }

    public static int GetSyllableToStateAction(SyllableMachine machine, int state) => machine switch
    {
        SyllableMachine.Indic => IndicSyllableMachineData.GetToStateAction(state),
        SyllableMachine.Use => UseSyllableMachineData.GetToStateAction(state),
        SyllableMachine.Myanmar => MyanmarSyllableMachineData.GetToStateAction(state),
        SyllableMachine.Khmer => KhmerSyllableMachineData.GetToStateAction(state),
        _ => throw new ArgumentOutOfRangeException(nameof(machine))
    };

    public static int GetSyllableFromStateAction(SyllableMachine machine, int state) => machine switch
    {
        SyllableMachine.Indic => IndicSyllableMachineData.GetFromStateAction(state),
        SyllableMachine.Use => UseSyllableMachineData.GetFromStateAction(state),
        SyllableMachine.Myanmar => MyanmarSyllableMachineData.GetFromStateAction(state),
        SyllableMachine.Khmer => KhmerSyllableMachineData.GetFromStateAction(state),
        _ => throw new ArgumentOutOfRangeException(nameof(machine))
    };

    public static (int Target, int Action)? GetSyllableEofTransition(
        SyllableMachine machine, int state)
    {
        int transition = machine switch
        {
            SyllableMachine.Indic => IndicSyllableMachineData.GetEofTransition(state),
            SyllableMachine.Use => UseSyllableMachineData.GetEofTransition(state),
            SyllableMachine.Myanmar => MyanmarSyllableMachineData.GetEofTransition(state),
            SyllableMachine.Khmer => KhmerSyllableMachineData.GetEofTransition(state),
            _ => throw new ArgumentOutOfRangeException(nameof(machine))
        };
        return transition < 0 ? null : GetTransition(machine, transition);
    }

    private static (int Target, int Action) GetTransition(SyllableMachine machine, int transition) => machine switch
    {
        SyllableMachine.Indic => (IndicSyllableMachineData.GetTarget(transition), IndicSyllableMachineData.GetAction(transition)),
        SyllableMachine.Use => (UseSyllableMachineData.GetTarget(transition), UseSyllableMachineData.GetAction(transition)),
        SyllableMachine.Myanmar => (MyanmarSyllableMachineData.GetTarget(transition), MyanmarSyllableMachineData.GetAction(transition)),
        SyllableMachine.Khmer => (KhmerSyllableMachineData.GetTarget(transition), KhmerSyllableMachineData.GetAction(transition)),
        _ => throw new ArgumentOutOfRangeException(nameof(machine))
    };

    private static bool IsScalar(uint codePoint) =>
        codePoint <= 0x10ffffu && (codePoint < 0xd800u || codePoint > 0xdfffu);
}
