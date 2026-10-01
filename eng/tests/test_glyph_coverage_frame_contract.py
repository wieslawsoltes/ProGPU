"""Device-free frame math and source contracts, not a hardware sampler oracle.

Captured native geometry supplies original bearings/extent/positions, while the
independent Fraction model derives exact pixel-to-texel coordinates and four-tap
weights. No expected final GPU colors or gamma tolerances are manufactured here.
"""

from fractions import Fraction as F
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
SHADERS = ROOT / "src/ProGPU.Backend/Shaders"
NATIVE = ROOT / "src/ProGPU.Native/src"


def original_frame(position, dpi, ratio=F(1), bearing=(-3, -18), size=(20, 22)):
    # Independent source contract: original bearing and raster extent are
    # physical; divide by DPI before original ratio/position, then physicalize.
    logical_min = tuple(p + F(b) / dpi * ratio for p, b in zip(position, bearing))
    logical_extent = tuple(F(s) / dpi * ratio for s in size)
    return tuple(x * dpi for x in logical_min + logical_extent)


def atlas_address(frame, fragment_center, atlas_min=(2, 2), atlas_span=(20, 22)):
    return tuple(F(a) + (p - origin) * F(span) / extent
                 for a, p, origin, span, extent in
                 zip(atlas_min, fragment_center, frame[:2], atlas_span, frame[2:]))


def four_tap_weights(texel_coord):
    # Linear pixel-center sampling footprint, independent of a GPU's finite
    # sampler arithmetic. Binary taps and dyadic fractions are tested separately.
    centered = tuple(x - F(1, 2) for x in texel_coord)
    fractions = tuple(x - x.numerator // x.denominator for x in centered)
    fx, fy = fractions
    return ((1-fx)*(1-fy), fx*(1-fy), (1-fx)*fy, fx*fy)


def exact_positive_axes(corners):
    q0, q1, q2, q3 = corners
    return (q0[1] == q1[1] and q1[0] == q2[0] and q2[1] == q3[1]
            and q3[0] == q0[0] and q2[0] > q0[0] and q2[1] > q0[1])


class GlyphCoverageFrameMathTests(unittest.TestCase):
    def test_captured_original_occurrence_frames(self):
        positions = ((F(65, 16), F(211, 16)), (F(17, 4), F(53, 4)))
        expected = ((F(41, 8), F(67, 8), F(20), F(22)),
                    (F(11, 2), F(17, 2), F(20), F(22)))
        for position, frame in zip(positions, expected):
            self.assertEqual(frame, original_frame(position, F(2)))

    def test_captured_fragment_coordinates_do_not_depend_on_canvas(self):
        frame = original_frame((F(65, 16), F(211, 16)), F(2))
        # Neither inverse projection nor target dimensions enter this address.
        for target_size in (96, 128):
            self.assertGreater(target_size, 30)
            self.assertEqual((F(59, 8), F(49, 8)),
                             atlas_address(frame, (F(21, 2), F(25, 2))))

    def test_independent_dyadic_four_tap_footprints(self):
        first = original_frame((F(65, 16), F(211, 16)), F(2))
        second = original_frame((F(17, 4), F(53, 4)), F(2))
        point = (F(21, 2), F(25, 2))
        self.assertEqual(tuple(F(n, 64) for n in (3, 21, 5, 35)),
                         four_tap_weights(atlas_address(first, point)))
        self.assertEqual((F(1, 4),) * 4,
                         four_tap_weights(atlas_address(second, point)))

    def test_binary_atlas_exact_linear_model(self):
        # A diagnostic binary atlas can distinguish coordinate correctness
        # without conflating R8 byte conversion, gamma or final blending.
        weights = tuple(F(n, 64) for n in (3, 21, 5, 35))
        for index in range(4):
            taps = tuple(int(i == index) for i in range(4))
            self.assertEqual(weights[index], sum(w * t for w, t in zip(weights, taps)))

    def test_original_dpi_and_ratio_are_not_rehinted(self):
        position = (F(65, 16), F(211, 16))
        for dpi in (F(1), F(5, 4), F(3, 2), F(2)):
            frame = original_frame(position, dpi)
            self.assertEqual((F(20), F(22)), frame[2:])
        self.assertEqual((F(10), F(11)), original_frame(position, F(2), F(1, 2))[2:])

    def test_exact_four_corner_admission_and_rejections(self):
        rectangle = ((F(2), F(3)), (F(12), F(3)),
                     (F(12), F(14)), (F(2), F(14)))
        self.assertTrue(exact_positive_axes(rectangle))
        tiny = F(1, 2**60)
        shear = tuple((x + y*tiny, y) for x, y in rectangle)
        self.assertFalse(exact_positive_axes(shear))
        self.assertFalse(exact_positive_axes((rectangle[0], (F(13), F(3)),
                                             rectangle[2], rectangle[3])))
        self.assertFalse(exact_positive_axes(tuple((-x, y) for x, y in rectangle)))
        self.assertFalse(exact_positive_axes(tuple((y, x) for x, y in rectangle)))

    def test_original_half_texel_clamp_and_closed_paint_support_remain_distinct(self):
        frame = original_frame((F(17, 4), F(53, 4)), F(2))
        maximum = (frame[0] + frame[2], frame[1] + frame[3])
        self.assertEqual((F(22), F(24)), atlas_address(frame, maximum))
        # Original Text hardware right/bottom ownership is half-open; bounded
        # paint's original frame guard remains closed and padded edge alpha0.
        self.assertEqual((F(43, 2), F(47, 2)), tuple(min(x, hi) for x, hi in
                         zip(atlas_address(frame, maximum), (F(43, 2), F(47, 2)))))


class GlyphCoverageFrameSourceTests(unittest.TestCase):
    def test_both_shader_routes_share_actual_fragment_address(self):
        geometry = (SHADERS / "TextGlyphGeometryCommon.wgsl").read_text()
        text = (SHADERS / "Text.wgsl").read_text()
        paint = (SHADERS / "HintedGlyphPaint.wgsl").read_text()
        self.assertIn("uniforms.pad0 == -1.0 && useMvp == 0.0", geometry)
        self.assertIn("output.textMode < 1.5 && exactPositiveAxes && finiteFrame", geometry)
        self.assertIn("q0 * uniforms.dpiScale, (q2 - q0) * uniforms.dpiScale", geometry)
        self.assertIn("(fragmentPosition - physicalFrame.xy) * (atlasSpan / physicalFrame.zw)", geometry)
        self.assertIn("return interpolated;", geometry)
        for source in (text, paint):
            self.assertIn("text_glyph_coverage_tex_coord", source)
            self.assertIn("input.position.xy", source)
            self.assertIn("@interpolate(flat) physicalGlyphFrame", source)
        self.assertNotIn("textureLoad", geometry)
        self.assertNotIn("round(", geometry)

    def test_existing_bounded_image_geometry_and_derivative_order(self):
        paint = (SHADERS / "HintedGlyphPaint.wgsl").read_text()
        for corner in ("paint.textureQuad01.xy", "paint.textureQuad01.zw",
                       "paint.textureQuad23.xy", "paint.textureQuad23.zw"):
            self.assertIn(corner, paint)
        self.assertIn("paintUV = mix(paint.uvBounds.xy, paint.uvBounds.zw, cornerUV)", paint)
        self.assertLess(paint.index("let paintDy = dpdy(input.paintUV)"),
                        paint.index("if (maskAlpha <= 0.0"))
        self.assertIn("any(glyphFrameUV > vec2<f32>(1.0))", paint)

    def test_actual_native_root_sites_and_uncertified_constructor(self):
        glyph = (NATIVE / "Backend/progpu_native_glyph_execution.cpp").read_text()
        scene = (NATIVE / "Scene/progpu_native_semantic_render_execution.cpp").read_text()
        pipeline = (NATIVE / "Backend/progpu_native_pipeline.cpp").read_text()
        constructor = pipeline.split("gpu_uniforms create_uniforms(", 1)[1].split("bool create_pipeline", 1)[0]
        self.assertNotIn("certify_root_glyph_coverage_frame", constructor)
        self.assertIn("gpu_uniforms uniforms{}", constructor)
        self.assertIn("!engine->semantic_glyph_draw_active && !use_group_layer", glyph)
        self.assertIn("if (!semantic_destination_sampling_active)", scene)
        for source in (glyph, scene):
            self.assertIn("certify_root_glyph_coverage_frame", source)
            self.assertIn("wgpuRenderPassEncoderSetViewport(pass, 0.0F, 0.0F", source)
        self.assertIn("target_layer == PROGPU_NATIVE_SCENE_NO_INDEX &&", scene)

    def test_certificate_is_exact_and_rejection_clears_prior_value(self):
        header = (NATIVE / "Backend/progpu_native_glyph_coverage_frame.hpp").read_text()
        self.assertLess(header.index("uniforms.pad0 = 0.0F"), header.index("if (target_width"))
        for proof in ("viewport_x != 0.0F", "viewport_y != 0.0F",
                      "uniforms.render_origin[0] != 0.0F",
                      "uniforms.model_view_projection[i] != identity",
                      "uniforms.view[i] != identity",
                      "uniforms.projection[i] != projection[i]",
                      "logical_width * dpi != physical_width",
                      "static_cast<double>(physical_width) != static_cast<double>(target_width)"):
            self.assertIn(proof, header)
        self.assertIn("offsetof(gpu_uniforms, pad0) == 204U", header)
        self.assertIn("sizeof(gpu_uniforms) == 224U", header)

    def test_negative_tag_preserves_original_texture_positive_policies(self):
        geometry = (SHADERS / "TextGlyphGeometryCommon.wgsl").read_text()
        texture = (SHADERS / "Texture.wgsl").read_text()
        self.assertIn("uniforms.pad0 == -1.0", geometry)
        self.assertIn("uniforms.boundedSourcePass > 0.5", texture)
        self.assertIn("uniforms.boundedSourcePass > 1.5", texture)
        self.assertFalse(-1.0 > 0.5)
        self.assertFalse(-1.0 > 1.5)
        self.assertTrue(1.0 > 0.5)
        self.assertTrue(2.0 > 1.5)

    def test_managed_actual_root_is_unique_and_other_glyph_uniforms_default_zero(self):
        compositor = (ROOT / "src/ProGPU.Scene/Compositor.cs").read_text()
        self.assertEqual(1, compositor.count("GlyphCoverageFramePolicy.GetRootCertificate("))
        self.assertIn("var rootViewport = NormalizeRenderTargetViewport(", compositor)
        self.assertIn("new Vector4(rootViewport.X, rootViewport.Y, rootViewport.Width, rootViewport.Height)", compositor)
        blocks = re.findall(r"(?:var (\w+) =|return) new GpuUniforms\s*\{(.*?)\n\s*\};",
                            compositor, re.DOTALL)
        self.assertEqual(4, len(blocks))
        for name, block in blocks:
            if "GetRootCertificate" in block:
                self.assertIn("Pad0 = GlyphCoverageFramePolicy", block)
            elif name == "sourceUniforms":
                # This distinct texture-only source has the ORIGINAL positive
                # tags, neither equal to the negative glyph certificate.
                self.assertIn("Pad0 = rasterOperation.IsEnabled ? 2f : 1f", block)
            else:
                self.assertNotIn("Pad0 =", block)
        policy = (ROOT / "src/ProGPU.Scene/GlyphCoverageFramePolicy.cs").read_text()
        self.assertIn("? -1f : 0f", policy)

    def test_managed_certificate_cache_key_tracks_actual_physical_extent(self):
        compositor = (ROOT / "src/ProGPU.Scene/Compositor.cs").read_text()
        self.assertIn("private uint _compiledScenePhysicalWidth;", compositor)
        self.assertIn("private uint _compiledScenePhysicalHeight;", compositor)
        self.assertIn("GetRootRenderTargetSize(width, height, out uint renderWidth, out uint renderHeight);", compositor)
        self.assertIn("_compiledScenePhysicalWidth != renderWidth || _compiledScenePhysicalHeight != renderHeight", compositor)
        self.assertIn("_compiledScenePhysicalWidth = physicalWidth;", compositor)
        self.assertIn("_compiledScenePhysicalHeight = physicalHeight;", compositor)

    def test_native_certificate_controls_are_in_normal_ctest_inventory(self):
        cmake = (ROOT / "src/ProGPU.Native/CMakeLists.txt").read_text()
        self.assertIn("tests/progpu_native_glyph_coverage_frame_tests.cpp", cmake)
        self.assertIn("add_test(NAME progpu_native_glyph_coverage_frame_tests COMMAND progpu_native_glyph_coverage_frame_tests)", cmake)


if __name__ == "__main__":
    unittest.main()
