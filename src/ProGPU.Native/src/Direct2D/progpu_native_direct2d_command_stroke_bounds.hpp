#pragma once

#include "progpu_native.h"
#include "progpu_native_com.hpp"
#include <d2d1.h>
#include <span>

namespace progpu::native::direct2d::detail {

// GetBounds omits hollow SDK figures, although they still contribute strokes.
// Query a separate bounds-only path made from the original captured lines and
// cubics. Marking its figures filled exposes their centerlines to the SDK query;
// the retained draw keeps its original hollow/closed/gap/join metadata.
// Time/temporary space: O(S + F), for captured segments and figures.
template<class Figure>
HRESULT command_stroke_centerline_bounds(ID2D1Factory* factory,
    std::span<const progpu_native_path_segment> segments,
    std::span<const Figure> figures, const D2D1_MATRIX_3X2_F* transform,
    D2D1_RECT_F& result) noexcept
{
    if (factory == nullptr) return E_INVALIDARG;
    com::pointer<ID2D1PathGeometry> geometry;
    HRESULT hr = factory->CreatePathGeometry(geometry.GetAddressOf());
    if (FAILED(hr) || !geometry) return FAILED(hr) ? hr : E_FAIL;
    com::pointer<ID2D1GeometrySink> sink;
    hr = geometry->Open(sink.GetAddressOf());
    if (FAILED(hr) || !sink) return FAILED(hr) ? hr : E_FAIL;
    sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    const auto point = [](const progpu_native_point& value) {
        return D2D1_POINT_2F{static_cast<FLOAT>(value.x), static_cast<FLOAT>(value.y)};
    };
    for (const auto& figure : figures) {
        if (figure.segment_offset > segments.size() ||
            figure.segment_count > segments.size() - figure.segment_offset)
            return E_INVALIDARG;
        if (figure.segment_count == 0U) continue;
        sink->BeginFigure(figure.start, D2D1_FIGURE_BEGIN_FILLED);
        for (const auto& segment : segments.subspan(figure.segment_offset, figure.segment_count)) {
            if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE) {
                sink->AddLine(point(segment.p1));
            } else if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
                const D2D1_BEZIER_SEGMENT cubic{point(segment.p1), point(segment.p2), point(segment.p3)};
                sink->AddBezier(cubic);
            } else {
                return E_INVALIDARG;
            }
        }
        sink->EndFigure(figure.closed ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
    }
    hr = sink->Close();
    if (FAILED(hr)) return hr;
    sink.Reset();
    D2D1_RECT_F candidate{};
    hr = geometry->GetBounds(transform, &candidate);
    if (SUCCEEDED(hr)) result = candidate;
    return hr;
}

} // namespace progpu::native::direct2d::detail
