using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace ProGPU.Media.Audio;

/// <summary>
/// Allocation-free intrinsic-SIMD conversion between planar and interleaved
/// stereo floating-point samples.
/// </summary>
/// <remarks>
/// Stereo layout conversion has independent four-frame lanes. ARM64 uses
/// ZIP/UZP and x86/x64 uses unpack/shuffle operations. Only the bounded tail
/// is scalar on supported hardware. General channel counts require a
/// channel-strided transpose and remain outside this stereo-specific helper.
/// </remarks>
internal static unsafe class MediaFloatStereoLayoutConverter
{
    internal static void Interleave(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        Span<float> destination)
    {
        if (right.Length != left.Length)
        {
            throw new ArgumentException(
                "The planar stereo channels have different frame counts.",
                nameof(right));
        }
        int sampleCount = checked(left.Length * 2);
        if (destination.Length < sampleCount)
        {
            throw new ArgumentException(
                "The interleaved destination is smaller than the planar stereo source.",
                nameof(destination));
        }

        int frame = 0;
        ref float leftStart = ref MemoryMarshal.GetReference(left);
        ref float rightStart = ref MemoryMarshal.GetReference(right);
        ref float destinationStart =
            ref MemoryMarshal.GetReference(destination);
        if (AdvSimd.Arm64.IsSupported)
        {
            frame = InterleaveArm64(
                left,
                right,
                destination);
        }
        else if (Sse.IsSupported)
        {
            for (; frame <= left.Length - Vector128<float>.Count;
                 frame += Vector128<float>.Count)
            {
                Vector128<float> leftSamples = Vector128.LoadUnsafe(
                    ref leftStart,
                    (nuint)frame);
                Vector128<float> rightSamples = Vector128.LoadUnsafe(
                    ref rightStart,
                    (nuint)frame);
                Sse.UnpackLow(leftSamples, rightSamples)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)(frame * 2));
                Sse.UnpackHigh(leftSamples, rightSamples)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)(frame * 2 + Vector128<float>.Count));
            }
        }

        for (; frame < left.Length; frame++)
        {
            destination[frame * 2] = left[frame];
            destination[frame * 2 + 1] = right[frame];
        }
    }

    internal static void Deinterleave(
        ReadOnlySpan<float> source,
        Span<float> left,
        Span<float> right)
    {
        if (right.Length != left.Length)
        {
            throw new ArgumentException(
                "The planar stereo destinations have different frame counts.",
                nameof(right));
        }
        int sampleCount = checked(left.Length * 2);
        if (source.Length < sampleCount)
        {
            throw new ArgumentException(
                "The interleaved source is smaller than the planar stereo destinations.",
                nameof(source));
        }

        int frame = 0;
        ref float sourceStart = ref MemoryMarshal.GetReference(source);
        ref float leftStart = ref MemoryMarshal.GetReference(left);
        ref float rightStart = ref MemoryMarshal.GetReference(right);
        if (AdvSimd.Arm64.IsSupported)
        {
            frame = DeinterleaveArm64(
                source,
                left,
                right);
        }
        else if (Sse.IsSupported)
        {
            for (; frame <= left.Length - Vector128<float>.Count;
                 frame += Vector128<float>.Count)
            {
                LoadInterleavedVectors(
                    ref sourceStart,
                    frame,
                    out Vector128<float> low,
                    out Vector128<float> high);
                Sse.Shuffle(low, high, 0x88)
                    .StoreUnsafe(ref leftStart, (nuint)frame);
                Sse.Shuffle(low, high, 0xDD)
                    .StoreUnsafe(ref rightStart, (nuint)frame);
            }
        }

        for (; frame < left.Length; frame++)
        {
            left[frame] = source[frame * 2];
            right[frame] = source[frame * 2 + 1];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int InterleaveArm64(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        Span<float> destination)
    {
        int frame = 0;
        fixed (float* leftPointer = left)
        fixed (float* rightPointer = right)
        fixed (float* destinationPointer = destination)
        {
            for (; frame <= left.Length - Vector128<float>.Count;
                 frame += Vector128<float>.Count)
            {
                AdvSimd.Arm64.StoreVectorAndZip(
                    destinationPointer + frame * 2,
                    (AdvSimd.LoadVector128(leftPointer + frame),
                     AdvSimd.LoadVector128(rightPointer + frame)));
            }
        }
        return frame;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int DeinterleaveArm64(
        ReadOnlySpan<float> source,
        Span<float> left,
        Span<float> right)
    {
        int frame = 0;
        fixed (float* sourcePointer = source)
        fixed (float* leftPointer = left)
        fixed (float* rightPointer = right)
        {
            for (; frame <= left.Length - Vector128<float>.Count;
                 frame += Vector128<float>.Count)
            {
                (Vector128<float> leftSamples,
                 Vector128<float> rightSamples) =
                    AdvSimd.Arm64.Load2xVector128AndUnzip(
                        sourcePointer + frame * 2);
                AdvSimd.Store(leftPointer + frame, leftSamples);
                AdvSimd.Store(rightPointer + frame, rightSamples);
            }
        }
        return frame;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadInterleavedVectors(
        ref float source,
        int frame,
        out Vector128<float> low,
        out Vector128<float> high)
    {
        int sample = frame * 2;
        low = Vector128.LoadUnsafe(ref source, (nuint)sample);
        high = Vector128.LoadUnsafe(
            ref source,
            (nuint)(sample + Vector128<float>.Count));
    }
}
