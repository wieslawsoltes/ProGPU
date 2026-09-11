#pragma once

#include "../Geometry/progpu_native_arc.hpp"

namespace progpu::native::text::svg_path_detail {

using point = geometry::arc_point;
using geometry::angle_within_sweep;
using geometry::equal;
using geometry::evaluate_arc;
using geometry::finite;
using geometry::resolve_arc;

} // namespace progpu::native::text::svg_path_detail
