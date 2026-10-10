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
#if defined(_XM_NO_INTRINSICS_)
constexpr std::uint32_t intrinsic_backend = 0U;
#elif defined(_XM_ARM_NEON_INTRINSICS_)
constexpr std::uint32_t intrinsic_backend = 2U;
#elif defined(_XM_SSE_INTRINSICS_)
constexpr std::uint32_t intrinsic_backend = 1U;
#else
constexpr std::uint32_t intrinsic_backend = 0U;
#endif
void store(std::array<float, output_count>& values, std::size_t offset, DirectX::FXMMATRIX matrix) {
    DirectX::XMFLOAT4X4 result{};
    DirectX::XMStoreFloat4x4(&result, matrix);
    std::memcpy(values.data() + offset, &result, sizeof(result));
}
float product(float first, float second) { volatile float result = first * second; return result; }
float sum(float first, float second) { volatile float result = first + second; return result; }
}

// Public SDK arithmetic controls, independent of all matrix captures. Exact
// binary inputs distinguish fused publication from separately rounded products.
extern "C" __declspec(dllexport) int __cdecl OriginalShaderArithmetic(
    const double* input, std::uint32_t inputs, float* output, std::uint32_t outputs,
    std::uint32_t* traits, std::uint32_t traits_size) noexcept {
    if (!input || !output || !traits || inputs != 12U || outputs != 8U || traits_size != trait_count) return 0;
    std::array<float, 12U> values{};
    for (std::size_t i = 0; i < values.size(); ++i) {
        if (!std::isfinite(input[i])) return 0;
        values[i] = static_cast<float>(input[i]);
        if (!std::isfinite(values[i])) return 0;
    }
    using namespace DirectX;
    const auto a = XMVectorSet(values[0], values[1], values[2], values[3]);
    const auto b = XMVectorSet(values[4], values[5], values[6], values[7]);
    const auto c = XMVectorSet(values[8], values[9], values[10], values[11]);
    XMFLOAT4 add{}, subtract{};
    XMStoreFloat4(&add, XMVectorMultiplyAdd(a, b, c));
    XMStoreFloat4(&subtract, XMVectorNegativeMultiplySubtract(a, b, c));
    std::array<float, 8U> candidate{};
    std::memcpy(candidate.data(), &add, sizeof(add));
    std::memcpy(candidate.data() + 4, &subtract, sizeof(subtract));
    for (const float value : candidate) if (!std::isfinite(value)) return 0;
    const std::array<std::uint32_t, trait_count> identity{_MSC_FULL_VER, DIRECTX_MATH_VERSION,
#if defined(_M_ARM64)
        0xAA64U, intrinsic_backend,
#else
        0x8664U, intrinsic_backend,
#endif
#if defined(_XM_FMA3_INTRINSICS_)
        1U
#else
        0U
#endif
    };
    std::memcpy(output, candidate.data(), sizeof(candidate));
    std::memcpy(traits, identity.data(), sizeof(identity));
    return 1;
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
        0xAA64U,intrinsic_backend,
#else
        0x8664U,intrinsic_backend,
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

// Independent additive original-SDK probe. Inputs are source local/parent
// MatrixTransform packets, visual offset, root DPI, inflated local LTRB and
// actual parent target XYWH. No ProGPU types, helpers or library are consumed.
extern "C" __declspec(dllexport) int __cdecl OriginalShaderAffineMath(
    const double* input, std::uint32_t inputs, float* output, std::uint32_t outputs,
    std::uint32_t* traits, std::uint32_t traits_size) noexcept {
    constexpr std::uint32_t affine_inputs=24U,affine_outputs=156U;
    if(!input || !output || !traits || inputs!=affine_inputs || outputs!=affine_outputs || traits_size!=trait_count) return 0;
    for(std::uint32_t i=0;i<inputs;++i) if(!std::isfinite(input[i])) return 0;
    if(input[14]<=0 || input[15]<=0 || input[18]<=input[16] || input[19]<=input[17] || input[22]<=0 || input[23]<=0) return 0;
    using namespace DirectX;
    const auto matrix=[&](std::size_t offset) {
        return XMMatrixSet(static_cast<float>(input[offset]),static_cast<float>(input[offset+1]),0,0,
            static_cast<float>(input[offset+2]),static_cast<float>(input[offset+3]),0,0,0,0,1,0,
            static_cast<float>(input[offset+4]),static_cast<float>(input[offset+5]),0,1);
    };
    const float dx=static_cast<float>((96*input[14])*(1.0/96)),dy=static_cast<float>((96*input[15])*(1.0/96));
    const auto root=XMMatrixScaling(dx,dy,1);
    const auto parent=XMMatrixMultiply(matrix(6),root);
    const auto placed=XMMatrixMultiply(XMMatrixTranslation(static_cast<float>(input[12]),static_cast<float>(input[13]),0),parent);
    const auto world=XMMatrixMultiply(matrix(0),placed);
    XMFLOAT4X4 w{}; XMStoreFloat4x4(&w,world);
    const float sx=std::sqrt(sum(product(w._11,w._11),product(w._12,w._12)));
    const float sy=std::sqrt(sum(product(w._21,w._21),product(w._22,w._22)));
    if(!(sx>0 && sy>0)) return 0;
    const auto scale=XMMatrixScaling(sx,sy,1);
    const auto inverse=XMMatrixInverse(nullptr,scale);
    const auto rest=XMMatrixMultiply(inverse,world);
    const float ax=std::floor(product(static_cast<float>(input[16]),sx));
    const float ay=std::floor(product(static_cast<float>(input[17]),sy));
    const float ex=std::ceil(product(static_cast<float>(input[18]),sx))-ax;
    const float ey=std::ceil(product(static_cast<float>(input[19]),sy))-ay;
    if(!(ex>0 && ey>0 && ex<=16384 && ey<=16384)) return 0;
    const auto final=XMMatrixMultiply(XMMatrixTranslation(ax,ay,0),rest);
    const auto unit=XMMatrixMultiply(XMMatrixScaling(ex,ey,1),final);
    const auto inverse_unit=XMMatrixInverse(nullptr,unit);
    const float rx=1.0F/static_cast<float>(input[22]),ry=1.0F/static_cast<float>(input[23]);
    const auto projection=XMMatrixSet(product(2,rx),0,0,0,0,product(-2,ry),0,0,0,0,1,0,-sum(1,rx),sum(1,ry),0,1);
    const auto localized=XMMatrixMultiply(unit,XMMatrixTranslation(-static_cast<float>(input[20]),-static_cast<float>(input[21]),0));
    const auto projected=XMMatrixMultiply(localized,projection);
    std::array<float,affine_outputs> candidate{};
    const auto retain=[&](std::size_t offset,FXMMATRIX value) {
        XMFLOAT4X4 stored{}; XMStoreFloat4x4(&stored,value);
        std::memcpy(candidate.data()+offset,&stored,sizeof(stored));
    };
    retain(0,world); retain(16,scale); retain(32,inverse); retain(48,rest);
    retain(64,final); retain(80,unit); retain(96,inverse_unit); retain(112,projection); retain(128,projected);
    candidate[144]=ax;candidate[145]=ay;candidate[146]=ex;candidate[147]=ey;
    for(std::size_t i=0;i<4;++i) candidate[148+i]=static_cast<float>(input[16+i]);
    candidate[152]=dx;candidate[153]=dy;candidate[154]=sx;candidate[155]=sy;
    for(const float value:candidate) if(!std::isfinite(value)) return 0;
    const std::array<std::uint32_t,trait_count> identity{_MSC_FULL_VER,DIRECTX_MATH_VERSION,
#if defined(_M_ARM64)
        0xAA64U,intrinsic_backend,
#else
        0x8664U,intrinsic_backend,
#endif
#if defined(_XM_FMA3_INTRINSICS_)
        1U
#else
        0U
#endif
    };
    std::memcpy(output,candidate.data(),sizeof(candidate)); std::memcpy(traits,identity.data(),sizeof(identity));
    return 1;
}

// Original resource construction only, called through installed SDK APIs.
// kind: 0 translate, 1 scale, 2 rotate, 3 skew. Four original parameters
// follow (first, second, centerX, centerY); rotation's second must be zero.
// No copied SDK trigonometric implementation and no ProGPU linkage.
extern "C" __declspec(dllexport) int __cdecl OriginalTransformPrimitiveMath(
    const double* input, std::uint32_t inputs, float* output, std::uint32_t outputs,
    std::uint32_t* traits, std::uint32_t traits_size) noexcept {
    if (!input || !output || !traits || inputs != 5U || outputs != 40U || traits_size != trait_count) return 0;
    for (std::uint32_t i=0;i<inputs;++i) if (!std::isfinite(input[i])) return 0;
    if (input[0] != 0 && input[0] != 1 && input[0] != 2 && input[0] != 3) return 0;
    if ((input[0] == 2 && input[2] != 0) || (input[0] == 0 && (input[3] != 0 || input[4] != 0))) return 0;
    using namespace DirectX;
    std::array<float,40U> candidate{};
    for (std::size_t i=0;i<4;++i) candidate[i]=static_cast<float>(input[1+i]);
    candidate[4]=candidate[0]; candidate[5]=candidate[1];
    auto core=XMMatrixIdentity();
    if (input[0] == 0) core=XMMatrixTranslation(candidate[0],candidate[1],0);
    else if (input[0] == 1) core=XMMatrixScaling(candidate[0],candidate[1],1);
    else {
        candidate[4]=static_cast<float>(std::fmod(input[1],360.0));
        candidate[5]=static_cast<float>(std::fmod(input[2],360.0));
        candidate[6]=XMConvertToRadians(candidate[4]); candidate[7]=XMConvertToRadians(candidate[5]);
        if (input[0] == 2) core=XMMatrixRotationZ(candidate[6]);
        else core=XMMatrixSet(1,static_cast<float>(std::tan(candidate[7])),0,0,
            static_cast<float>(std::tan(candidate[6])),1,0,0,0,0,1,0,0,0,0,1);
    }
    const auto centered=XMMatrixMultiply(XMMatrixMultiply(
        XMMatrixTranslation(static_cast<float>(-input[3]),static_cast<float>(-input[4]),0),core),
        XMMatrixTranslation(candidate[2],candidate[3],0));
    XMFLOAT4X4 matrix{}; XMStoreFloat4x4(&matrix,core); std::memcpy(candidate.data()+8,&matrix,sizeof(matrix));
    XMStoreFloat4x4(&matrix,centered); std::memcpy(candidate.data()+24,&matrix,sizeof(matrix));
    for (float value:candidate) if (!std::isfinite(value)) return 0;
    const std::array<std::uint32_t,trait_count> identity{_MSC_FULL_VER,DIRECTX_MATH_VERSION,
#if defined(_M_ARM64)
        0xAA64U,intrinsic_backend,
#else
        0x8664U,intrinsic_backend,
#endif
#if defined(_XM_FMA3_INTRINSICS_)
        1U
#else
        0U
#endif
    };
    std::memcpy(output,candidate.data(),sizeof(candidate)); std::memcpy(traits,identity.data(),sizeof(identity));
    return 1;
}
