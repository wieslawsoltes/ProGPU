//! Safe adapter to the isolated original FreeType C ABI ownership crate.
//! The rendering crate retains its forbid(unsafe_code) policy unchanged.
use super::*;
use progpu_freetype as ft;

/// Actual pinned native factory. Construction performs no font execution; the
/// existing cache/face producer invokes original dependency admission first.
#[derive(Debug, Default)]
pub struct FreeTypeBackendFactory;
impl FreeTypeBackendFactory {
    pub fn new() -> Self {
        Self
    }
}
/// Actual FT_Init_FreeType owner, kept separate until consumed by one face.
#[derive(Debug)]
pub struct FreeTypeLibraryBackend {
    library: ft::Library,
}
/// Exclusive actual face plus copied MM inventory. Its immutable source memory
/// survives native face/library teardown, and the outer face retains SfntFace's
/// exact original source identity independently of any bounded-storage copy.
#[derive(Debug)]
pub struct FreeTypeFaceBackend {
    face: ft::Face,
    axes: Vec<NativeHintedAxis>,
    phase_fault: Option<NativeHintedCaptureError>,
}
/// Connected original sixteen-entry face/latest-batch cache over actual FreeType.
pub type FreeTypeHintedFontCache = NativeHintedFontCache<FreeTypeBackendFactory>;

fn error(error: ft::Error) -> NativeHintedCaptureError {
    match error {
        ft::Error::InvalidArgument => NativeHintedCaptureError::InvalidArgument,
        ft::Error::InvalidFont => NativeHintedCaptureError::InvalidFont,
        ft::Error::UnsupportedFont => NativeHintedCaptureError::UnsupportedFont,
        ft::Error::DependencyMismatch => NativeHintedCaptureError::DependencyMismatch,
        ft::Error::ResourceExhausted => NativeHintedCaptureError::ResourceExhausted,
        ft::Error::HintingFailed | ft::Error::FreeType(_) => {
            NativeHintedCaptureError::HintingFailed
        }
    }
}
impl NativeHintedBackendFactory for FreeTypeBackendFactory {
    type Library = FreeTypeLibraryBackend;
    fn dependency_evidence(
        &mut self,
        target: NativeHintedTarget,
    ) -> Result<NativeHintedDependencyEvidence, NativeHintedCaptureError> {
        let actual = ft::dependency_evidence().map_err(error)?;
        let receipt = actual.receipt;
        if receipt.rid != target.rid() {
            return Err(NativeHintedCaptureError::DependencyMismatch);
        }
        // These source constants are independently enforced by the original
        // full build verifier. The digests below are generated only from its
        // actual validated archive/header/notice/source installation.
        Ok(NativeHintedDependencyEvidence {
            receipt: Arc::new(NativeHintedDependencyReceipt {
                target,
                pin: NativeHintedDependencyPin {
                    repository: "https://gitlab.freedesktop.org/freetype/freetype.git".into(),
                    tag: "VER-2-14-3".into(),
                    version: [2, 14, 3],
                    tag_object: "c740f0fda4274d6ffd2e5b64a25b06ef69803a07".into(),
                    commit: "0a0221a1347e2f1e07c395263540026e9a0aa7c7".into(),
                    signer_fingerprint: "58E0C111E39F5408C5D3EC76C1A60EACE707FDA5".into(),
                    author: "Werner Lemberg <wl@gnu.org>".into(),
                    license: "FTL".into(),
                    static_library: true,
                    optional_dependencies_disabled: true,
                },
                manifest_sha256: receipt.manifest_sha256,
                archive_sha256: receipt.archive_sha256,
                headers_sha256: receipt.headers_sha256,
                notices_sha256: receipt.notices_sha256,
            }),
            producer_image: actual.producer_image,
            function_images: actual.function_images,
            header_version: receipt.header_version,
        })
    }
    fn initialize_library(&mut self) -> Result<Self::Library, NativeHintedCaptureError> {
        Ok(FreeTypeLibraryBackend {
            library: ft::Library::new().map_err(error)?,
        })
    }
}
impl NativeHintedLibraryBackend for FreeTypeLibraryBackend {
    type Face = FreeTypeFaceBackend;
    fn version(&self) -> [u32; 3] {
        self.library.version()
    }
    fn set_interpreter(
        &mut self,
        policy: NativeHintPolicy,
    ) -> Result<(), NativeHintedCaptureError> {
        self.library
            .set_interpreter(match policy {
                NativeHintPolicy::TrueType35 => ft::Interpreter::TrueType35,
                NativeHintPolicy::TrueType40 => ft::Interpreter::TrueType40,
            })
            .map_err(error)
    }
    fn interpreter(&self) -> Result<u32, NativeHintedCaptureError> {
        self.library.interpreter().map_err(error)
    }
    fn open_memory_face(self, font: SfntFace) -> Result<Self::Face, NativeHintedCaptureError> {
        // Whole owned sources share their exact Arc. Embedded/bounded/custom
        // storage is frozen into immutable bytes for the native pointer lifetime;
        // the existing outer SfntFace lease and batch identity remain unchanged.
        let bytes = font.storage().to_owned_bytes();
        let face = self
            .library
            .open_memory_face(bytes, font.face_index())
            .map_err(error)?;
        Ok(FreeTypeFaceBackend {
            face,
            axes: Vec::new(),
            phase_fault: None,
        })
    }
}
impl NativeHintedFaceBackend for FreeTypeFaceBackend {
    fn information(&self) -> NativeHintedFaceInfo {
        let actual = self.face.information();
        NativeHintedFaceInfo {
            true_type_format: actual.true_type_format,
            scalable: actual.scalable,
            color: actual.color,
            glyph_count: actual.glyph_count,
            multiple_masters: actual.multiple_masters,
        }
    }
    fn variation_axes(&mut self) -> Result<&[NativeHintedAxis], NativeHintedCaptureError> {
        let actual = self.face.variation_axes().map_err(error)?;
        let mut axes = Vec::new();
        axes.try_reserve_exact(actual.len())
            .map_err(|_| NativeHintedCaptureError::ResourceExhausted)?;
        axes.extend(actual.iter().map(|axis| NativeHintedAxis {
            minimum: axis.minimum,
            default: axis.default,
            maximum: axis.maximum,
        }));
        self.axes = axes;
        Ok(&self.axes)
    }
    fn set_variations(
        &mut self,
        coordinates_16_16: &[i32],
    ) -> Result<(), NativeHintedCaptureError> {
        self.face.set_variations(coordinates_16_16).map_err(error)
    }
    fn request_nominal_size(
        &mut self,
        pixels_per_em_26_6: [u32; 2],
    ) -> Result<NativeHintedMetrics, NativeHintedCaptureError> {
        let actual = self
            .face
            .request_nominal_size(pixels_per_em_26_6)
            .map_err(error)?;
        Ok(NativeHintedMetrics {
            units_per_em: actual.units_per_em,
            pixels_per_em: actual.pixels_per_em,
            scale_16_16: actual.scale_16_16,
            driver_line_metrics_26_6: actual.driver_line_metrics_26_6,
            design_line_metrics: actual.design_line_metrics,
        })
    }
    fn set_phase(&mut self, phase_26_6: [u32; 2]) {
        // Original factory admission already proves this range. If this public
        // backend trait is used independently, retain a failed selection so no
        // subsequent load can accidentally publish the prior native phase.
        if let Err(fault) = self.face.set_phase(phase_26_6) {
            self.phase_fault = Some(error(fault));
        }
    }
    fn capture_glyph(
        &mut self,
        glyph: u32,
        flags: NativeHintedLoadFlags,
    ) -> Result<NativeHintedGlyph, NativeHintedCaptureError> {
        if let Some(fault) = self.phase_fault {
            return Err(fault);
        }
        let actual = self
            .face
            .capture_glyph(glyph, outline_target(flags)?)
            .map_err(error)?;
        let m = actual.metrics;
        Ok(NativeHintedGlyph {
            glyph_id: actual.glyph_id,
            advance_26_6: m.advance_26_6,
            horizontal_bearing_26_6: m.horizontal_bearing_26_6,
            size_26_6: m.size_26_6,
            horizontal_advance_26_6: m.horizontal_advance_26_6,
            vertical_bearing_26_6: m.vertical_bearing_26_6,
            vertical_advance_26_6: m.vertical_advance_26_6,
            linear_advances_16_16: m.linear_advances_16_16,
            side_bearing_deltas_26_6: m.side_bearing_deltas_26_6,
            outline_flags: actual.outline_flags,
            points_26_6: actual.points_26_6,
            tags: actual.tags,
            contour_ends: actual.contour_ends,
        })
    }
    fn load_glyph(
        &mut self,
        glyph: u32,
        flags: NativeHintedLoadFlags,
    ) -> Result<NativeHintedSlot<'_>, NativeHintedCaptureError> {
        if let Some(fault) = self.phase_fault {
            return Err(fault);
        }
        let target = outline_target(flags)?;
        let actual = self.face.load_glyph(glyph, target).map_err(error)?;
        let m = actual.metrics;
        Ok(NativeHintedSlot {
            glyph_id: actual.glyph_id,
            outline_format: true,
            outline_check_succeeded: true,
            metrics: NativeHintedSlotMetrics {
                advance_26_6: m.advance_26_6,
                horizontal_bearing_26_6: m.horizontal_bearing_26_6,
                size_26_6: m.size_26_6,
                horizontal_advance_26_6: m.horizontal_advance_26_6,
                vertical_bearing_26_6: m.vertical_bearing_26_6,
                vertical_advance_26_6: m.vertical_advance_26_6,
                linear_advances_16_16: m.linear_advances_16_16,
                side_bearing_deltas_26_6: m.side_bearing_deltas_26_6,
            },
            outline_flags: actual.outline_flags,
            points_26_6: &actual.points_26_6,
            tags: &actual.tags,
            contour_ends: &actual.contour_ends,
        })
    }
}

fn outline_target(
    flags: NativeHintedLoadFlags,
) -> Result<ft::OutlineTarget, NativeHintedCaptureError> {
    [
        ft::OutlineTarget::Normal,
        ft::OutlineTarget::Monochrome,
        ft::OutlineTarget::HorizontalLcd,
        ft::OutlineTarget::Unhinted,
    ]
    .into_iter()
    .find(|target| target.load_flags() as u32 == flags.bits())
    .ok_or(NativeHintedCaptureError::InvalidArgument)
}
