#pragma once

#include "progpu_native_mil.hpp"
#include "../Geometry/progpu_native_curve_dash.hpp"

// The curve dash runs moved to the neutral Geometry module; MIL keeps its
// spelling of the namespace.
namespace progpu::native::mil {

namespace curve_dash = ::progpu::native::geometry::curve_dash;

} // namespace progpu::native::mil
