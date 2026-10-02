#pragma once

#include "progpu_native_source_transform_primitive.hpp"

inline bool source_transform_primitive_arithmetic() {
    using namespace progpu::native::shader_effect;
    const auto bits = [](float value) { return std::bit_cast<std::uint32_t>(value); };
    source_angle angle{};
    UV_REQUIRE(reduce_source_angle(90.0,angle));
    UV_REQUIRE(bits(angle.radians)==0x3FC90FDBU);
    UV_REQUIRE(reduce_source_angle(360000000.25,angle));
    UV_REQUIRE(angle.degrees==.25F && bits(angle.radians)==0x3B8EFA35U);
    UV_REQUIRE(reduce_source_angle(-0.0,angle));
    UV_REQUIRE(bits(angle.degrees)==0x80000000U && bits(angle.radians)==0x80000000U);
    const auto prior_angle=angle;
    UV_REQUIRE(!reduce_source_angle(std::numeric_limits<double>::infinity(),angle));
    UV_REQUIRE(std::memcmp(&angle,&prior_angle,sizeof(angle))==0);
    UV_REQUIRE(!reduce_source_angle(std::numeric_limits<double>::quiet_NaN(),angle));
    UV_REQUIRE(std::memcmp(&angle,&prior_angle,sizeof(angle))==0);
    axis_matrix centered{};
    UV_REQUIRE(center_source_primitive({1.25F,-.5F},3.25F,-4.5F,centered));
    UV_REQUIRE(centered.x==1.25F && centered.y==-.5F && centered.tx==-.8125F && centered.ty==-6.75F);
    // Observed core values are INPUT to centering, not a rotation lookup table.
    const axis_matrix quarter{-0x1p-23F,-0x1p-23F,1,1,0,0,0x1.fffffcp-1F,-0x1.fffffcp-1F};
    UV_REQUIRE(center_source_primitive(quarter,3.25F,-4.5F,centered));
    UV_REQUIRE(bits(centered.tx)==0xBF9FFFF8U && bits(centered.ty)==0xC0F80000U);
    const auto prior=centered;
    UV_REQUIRE(!center_source_primitive({},std::numeric_limits<float>::infinity(),0,centered));
    UV_REQUIRE(std::memcmp(&centered,&prior,sizeof(centered))==0);
    UV_REQUIRE(!center_source_primitive({std::numeric_limits<float>::max(),1},2,0,centered));
    UV_REQUIRE(std::memcmp(&centered,&prior,sizeof(centered))==0);
    return true;
}
