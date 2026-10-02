// Independent original-SDK observation companion. No ProGPU renderer/linkage
// and no copied DirectXMath implementation: call the installed original API.
#include <DirectXMath.h>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>

#if !defined(_M_X64) && !defined(_M_ARM64)
#error Only native Windows x64 and ARM64 reference architectures are admitted.
#endif

namespace {
constexpr std::uint32_t input_count = 14U, output_count = 128U, trait_count = 5U;
void store(std::array<float, output_count>& values, std::size_t offset, DirectX::FXMMATRIX matrix) {
    DirectX::XMFLOAT4X4 result{};
    DirectX::XMStoreFloat4x4(&result, matrix);
    std::memcpy(values.data() + offset, &result, sizeof(result));
}
float product(float first, float second) { volatile float result = first * second; return result; }
float sum(float first, float second) { volatile float result = first + second; return result; }
}

extern "C" __declspec(dllexport) int __cdecl OriginalShaderAxisMath(
    const double* input, std::uint32_t inputs, float* output, std::uint32_t outputs,
    std::uint32_t* traits, std::uint32_t traits_size) noexcept {
    if (input == nullptr || output == nullptr || traits == nullptr || inputs != input_count ||
        outputs != output_count || traits_size != trait_count) return 0;
    for (std::uint32_t i = 0; i < inputs; ++i) if (!std::isfinite(input[i])) return 0;
    if (input[0] <= 0 || input[1] <= 0 || input[2] <= 0 || input[3] <= 0 ||
        input[8] <= 0 || input[9] <= 0) return 0;
    for (std::uint32_t i = 10; i < inputs; ++i) if (input[i] < 0) return 0;
    using namespace DirectX;
    // Original RTB creates its root from double dpi*(1/96), then the matrix
    // resource narrows to float. Source local transform follows placement.
    const float dx = static_cast<float>((96.0 * input[0]) * (1.0 / 96.0));
    const float dy = static_cast<float>((96.0 * input[1]) * (1.0 / 96.0));
    const auto root = XMMatrixScaling(dx, dy, 1.0F);
    const auto placement = XMMatrixTranslation(static_cast<float>(input[4]), static_cast<float>(input[5]), 0.0F);
    const auto local = XMMatrixScaling(static_cast<float>(input[2]), static_cast<float>(input[3]), 1.0F);
    const auto world = XMMatrixMultiply(local, XMMatrixMultiply(placement, root));
    XMFLOAT4X4 w{}; XMStoreFloat4x4(&w, world);
    // BaseMatrix uses scalar sqrtf here, not XMVectorSqrt's NEON estimate.
    const float sx = std::sqrt(sum(product(w._11,w._11),product(w._12,w._12)));
    const float sy = std::sqrt(sum(product(w._21,w._21),product(w._22,w._22)));
    const auto scale = XMMatrixScaling(sx,sy,1.0F);
    XMVECTOR determinant{};
    const auto inverse = XMMatrixInverse(&determinant,scale);
    const auto rest = XMMatrixMultiply(inverse,world);
    const float l = sum(static_cast<float>(input[6]),-static_cast<float>(input[12]));
    const float t = sum(static_cast<float>(input[7]),-static_cast<float>(input[10]));
    const float r = sum(static_cast<float>(input[6]+input[8]),static_cast<float>(input[13]));
    const float b = sum(static_cast<float>(input[7]+input[9]),static_cast<float>(input[11]));
    const float ax = std::floor(product(l,sx)), ay = std::floor(product(t,sy));
    const float ex = std::ceil(product(r,sx))-ax, ey = std::ceil(product(b,sy))-ay;
    if (!(ex > 0 && ex <= 16384 && ey > 0 && ey <= 16384)) return 0;
    const auto final = XMMatrixMultiply(XMMatrixTranslation(ax,ay,0.0F),rest);
    const auto sampling = XMMatrixMultiply(XMMatrixScaling(ex,ey,1.0F),final);
    const auto sampling_inverse = XMMatrixInverse(nullptr,sampling);
    std::array<float,output_count> candidate{};
    store(candidate,0,world); store(candidate,16,scale); store(candidate,32,inverse);
    store(candidate,48,rest); store(candidate,64,final); store(candidate,80,sampling);
    store(candidate,96,sampling_inverse);
    candidate[112]=ax; candidate[113]=ay; candidate[114]=ex; candidate[115]=ey;
    candidate[116]=l; candidate[117]=t; candidate[118]=r; candidate[119]=b;
    candidate[120]=sx; candidate[121]=sy; candidate[122]=XMVectorGetX(determinant);
    candidate[123]=dx; candidate[124]=dy;
    for (const float value : candidate) if (!std::isfinite(value)) return 0;
    std::array<std::uint32_t,trait_count> identity{_MSC_FULL_VER,DIRECTX_MATH_VERSION,
#if defined(_M_ARM64)
        0xAA64U,2U,
#else
        0x8664U,1U,
#endif
#if defined(_XM_FMA3_INTRINSICS_)
        1U
#else
        0U
#endif
    };
    std::memcpy(output,candidate.data(),sizeof(candidate));
    std::memcpy(traits,identity.data(),sizeof(identity));
    return 1;
}
