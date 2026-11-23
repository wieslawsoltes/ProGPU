//! Original native library/face selection and sequential immediate slot capture.
use super::*;

/// Exact prepared native desktop RID. The original producer excludes browser,
/// 32-bit and unsupported OS/architecture combinations independently of rendering.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeHintedTarget {
    WindowsX64,
    WindowsArm64,
    MacOsX64,
    MacOsArm64,
    LinuxX64,
    LinuxArm64,
}
impl NativeHintedTarget {
    /// Original admitted RID of this compiled target, without host inference.
    pub fn current() -> Option<Self> {
        if !cfg!(target_pointer_width = "64") {
            return None;
        }
        match (std::env::consts::OS, std::env::consts::ARCH) {
            ("windows", "x86_64") => Some(Self::WindowsX64),
            ("windows", "aarch64") => Some(Self::WindowsArm64),
            ("macos", "x86_64") => Some(Self::MacOsX64),
            ("macos", "aarch64") => Some(Self::MacOsArm64),
            ("linux", "x86_64") => Some(Self::LinuxX64),
            ("linux", "aarch64") => Some(Self::LinuxArm64),
            _ => None,
        }
    }
    /// Original receipt/runtime identifier.
    pub const fn rid(self) -> &'static str {
        match self {
            Self::WindowsX64 => "win-x64",
            Self::WindowsArm64 => "win-arm64",
            Self::MacOsX64 => "osx-x64",
            Self::MacOsArm64 => "osx-arm64",
            Self::LinuxX64 => "linux-x64",
            Self::LinuxArm64 => "linux-arm64",
        }
    }
}
/// Original public entry-point order whose loaded image identity must be exact.
pub const NATIVE_HINTED_DEPENDENCY_FUNCTIONS: [&str; 17] = [
    "FT_Init_FreeType",
    "FT_Done_FreeType",
    "FT_Library_Version",
    "FT_Property_Set",
    "FT_Property_Get",
    "FT_New_Memory_Face",
    "FT_Done_Face",
    "FT_Get_Font_Format",
    "FT_Get_MM_Var",
    "FT_Done_MM_Var",
    "FT_Set_Var_Design_Coordinates",
    "FT_Request_Size",
    "FT_Set_Transform",
    "FT_Load_Glyph",
    "FT_Outline_Check",
    "FT_Get_Char_Index",
    "FT_MulFix",
];
/// Exact original signed external source pin. This is identity metadata, not a
/// signature verifier, package receipt or evidence that a library was loaded.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NativeHintedDependencyPin {
    /// Original source repository URL.
    pub repository: String,
    /// Original signed release tag name.
    pub tag: String,
    /// Original version and tag.
    pub version: [u32; 3],
    /// Original signed tag object, full lowercase hexadecimal.
    pub tag_object: String,
    /// Original peeled release commit, full lowercase hexadecimal.
    pub commit: String,
    /// Original signer fingerprint, full uppercase hexadecimal.
    pub signer_fingerprint: String,
    /// Original release author identity.
    pub author: String,
    /// Original redistribution license selection.
    pub license: String,
    /// Actual prepared archive is static, not an ambient shared dependency.
    pub static_library: bool,
    /// All original optional dependencies were disabled in the isolated build.
    pub optional_dependencies_disabled: bool,
}
impl NativeHintedDependencyPin {
    fn original(&self) -> bool {
        self.version == [2, 14, 3]
            && self.repository == "https://gitlab.freedesktop.org/freetype/freetype.git"
            && self.tag == "VER-2-14-3"
            && self.tag_object == "c740f0fda4274d6ffd2e5b64a25b06ef69803a07"
            && self.commit == "0a0221a1347e2f1e07c395263540026e9a0aa7c7"
            && self.signer_fingerprint == "58E0C111E39F5408C5D3EC76C1A60EACE707FDA5"
            && self.author == "Werner Lemberg <wl@gnu.org>"
            && self.license == "FTL"
            && self.static_library
            && self.optional_dependencies_disabled
    }
}
/// Retained result of the backend's actual external receipt verification. The
/// backend must verify the original signed source, archive object architecture,
/// static isolated build, complete headers/notices and original producer probes;
/// filling these metadata fields alone does not perform those checks.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NativeHintedDependencyReceipt {
    /// Actual architecture selected by the external build, not the caller's wish.
    pub target: NativeHintedTarget,
    /// Original reviewed external source identity.
    pub pin: NativeHintedDependencyPin,
    /// Exact verified manifest bytes' SHA-256.
    pub manifest_sha256: [u8; 32],
    /// Exact verified static archive bytes' SHA-256.
    pub archive_sha256: [u8; 32],
    /// Verified complete header inventory receipt SHA-256.
    pub headers_sha256: [u8; 32],
    /// Verified complete redistribution notice inventory receipt SHA-256.
    pub notices_sha256: [u8; 32],
}
/// Actual loaded-image observation from the backend that owns the original API.
/// Tokens are opaque owner identities observed by the platform module lookup,
/// never user-selected handles or addresses dereferenced by this safe module.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NativeHintedDependencyEvidence {
    /// Actual verified prepared installation retained by the backend.
    pub receipt: Arc<NativeHintedDependencyReceipt>,
    /// Original executing producer's loaded image identity; zero is unavailable.
    pub producer_image: usize,
    /// Actual loaded image for every entry in NATIVE_HINTED_DEPENDENCY_FUNCTIONS.
    pub function_images: [usize; 17],
    /// Actual compile-time dependency header version.
    pub header_version: [u32; 3],
}
impl NativeHintedDependencyEvidence {
    fn admit(&self, target: NativeHintedTarget) -> Result<(), NativeHintedCaptureError> {
        if self.receipt.target != target
            || !self.receipt.pin.original()
            || self.producer_image == 0
            || self
                .function_images
                .iter()
                .any(|&owner| owner != self.producer_image)
        {
            return Err(NativeHintedCaptureError::DependencyMismatch);
        }
        Ok(())
    }
}
/// Actual backend face inventory, queried before variation/size selection.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeHintedFaceInfo {
    /// Exact original format equality with "TrueType".
    pub true_type_format: bool,
    /// Original scalable outline flag.
    pub scalable: bool,
    /// Original face color flag; color faces remain outside native hint admission.
    pub color: bool,
    /// Actual original driver glyph count before unsigned narrowing.
    pub glyph_count: i64,
    /// Original multiple-masters face flag, independent of axis count.
    pub multiple_masters: bool,
}
/// Actual original driver axis in fvar order, retaining native fixed endpoints.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeHintedAxis {
    /// Actual minimum raw 16.16 coordinate.
    pub minimum: i64,
    /// Actual default raw 16.16 coordinate.
    pub default: i64,
    /// Actual maximum raw 16.16 coordinate.
    pub maximum: i64,
}
/// Exact immutable original load flags; no bitmap or auto-hinter route is exposed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeHintedLoadFlags(u32);
impl NativeHintedLoadFlags {
    /// Original FT_LOAD_NO_BITMAP | NO_AUTOHINT | PEDANTIC and selected target.
    pub fn for_policy(policy: NativeOutlineLoadPolicy) -> Self {
        let common = (1 << 3) | (1 << 15) | (1 << 7);
        Self(
            common
                | match policy {
                    NativeOutlineLoadPolicy::Normal => 0,
                    NativeOutlineLoadPolicy::Monochrome => 2 << 16,
                    NativeOutlineLoadPolicy::HorizontalLcd => 3 << 16,
                    NativeOutlineLoadPolicy::Unhinted => 1 << 1,
                },
        )
    }
    /// Exact original dependency load-flag bits.
    pub fn bits(self) -> u32 {
        self.0
    }
}
/// Actual driver slot metrics, copied without scaling, phase removal or rounding.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeHintedSlotMetrics {
    /// Original slot advance vector.
    pub advance_26_6: [i64; 2],
    /// Original horizontal bearings.
    pub horizontal_bearing_26_6: [i64; 2],
    /// Original width and height.
    pub size_26_6: [i64; 2],
    /// Original horizontal advance.
    pub horizontal_advance_26_6: i64,
    /// Original vertical bearings.
    pub vertical_bearing_26_6: [i64; 2],
    /// Original vertical advance.
    pub vertical_advance_26_6: i64,
    /// Original linear horizontal/vertical advances.
    pub linear_advances_16_16: [i64; 2],
    /// Original left/right side-bearing deltas.
    pub side_bearing_deltas_26_6: [i64; 2],
}
/// Borrowed actual slot under the backend face's exclusive load borrow. The next
/// load cannot begin until immediate capture has finished. No manufactured bytes
/// or unhinted geometry satisfy this producer interface.
#[derive(Clone, Copy, Debug)]
pub struct NativeHintedSlot<'a> {
    /// Exact actual slot glyph index after the requested load.
    pub glyph_id: u32,
    /// Actual slot format equals FT_GLYPH_FORMAT_OUTLINE.
    pub outline_format: bool,
    /// Original FT_Outline_Check succeeded for the exact borrowed outline.
    pub outline_check_succeeded: bool,
    /// Actual raw slot metrics.
    pub metrics: NativeHintedSlotMetrics,
    /// Original unchanged outline flags.
    pub outline_flags: i32,
    /// Actual captured integer coordinates, with the selected physical phase.
    pub points_26_6: &'a [[i64; 2]],
    /// Actual original tags, one per point.
    pub tags: &'a [u8],
    /// Actual original inclusive glyph-local contour ends.
    pub contour_ends: &'a [i16],
}
/// Safe ownership interface for one actual initialized original library. Opening
/// a face consumes it: the returned backend must retain this library until its
/// face is destroyed, then retire the library before the source memory owner.
pub trait NativeHintedLibraryBackend {
    /// Exclusive actual face implementation that owns this library.
    type Face: NativeHintedFaceBackend;
    /// Actual FT_Library_Version result.
    fn version(&self) -> [u32; 3];
    /// Actual original interpreter property selection.
    fn set_interpreter(&mut self, policy: NativeHintPolicy)
    -> Result<(), NativeHintedCaptureError>;
    /// Actual property readback, not the most recent requested value.
    fn interpreter(&self) -> Result<u32, NativeHintedCaptureError>;
    /// Actual FT_New_Memory_Face over these exact retained bytes and face index.
    fn open_memory_face(self, font: SfntFace) -> Result<Self::Face, NativeHintedCaptureError>;
}
/// Required actual driver implementation. It must own the face/library/source,
/// translate original failures, preserve raw output, and execute only the exact
/// selected flags. The opt-in pinned FreeType adapter supplies the actual
/// original dependency; portable builds keep this explicit provider boundary.
pub trait NativeHintedFaceBackend {
    /// Query the actual current face inventory.
    fn information(&self) -> NativeHintedFaceInfo;
    /// Borrow the complete actual MM axis inventory; failed/absent metadata fails.
    fn variation_axes(&mut self) -> Result<&[NativeHintedAxis], NativeHintedCaptureError>;
    /// Actual original FT_Set_Var_Design_Coordinates on all supplied axes.
    fn set_variations(&mut self, coordinates_16_16: &[i32])
    -> Result<(), NativeHintedCaptureError>;
    /// Actual nominal FT_Request_Size with independent original 26.6 sizes, zero
    /// horizontal/vertical resolution; return the actual resulting frame.
    fn request_nominal_size(
        &mut self,
        pixels_per_em_26_6: [u32; 2],
    ) -> Result<NativeHintedMetrics, NativeHintedCaptureError>;
    /// Actual null-transform FT_Set_Transform with the exact original phase.
    fn set_phase(&mut self, phase_26_6: [u32; 2]);
    /// Execute one actual original load and borrow its resulting slot immediately.
    fn load_glyph(
        &mut self,
        glyph: u32,
        flags: NativeHintedLoadFlags,
    ) -> Result<NativeHintedSlot<'_>, NativeHintedCaptureError>;
    /// Transfer an actual already-checked owned slot without a second array copy.
    /// Backends exposing only a borrowed slot keep the original immediate-copy
    /// behavior. The owned route must preserve the same actual format/check proof.
    fn capture_glyph(
        &mut self,
        glyph: u32,
        flags: NativeHintedLoadFlags,
    ) -> Result<NativeHintedGlyph, NativeHintedCaptureError> {
        let slot = self
            .load_glyph(glyph, flags)
            .map_err(|_| NativeHintedCaptureError::HintingFailed)?;
        capture_slot(glyph, slot)
    }
}
/// Actual external dependency factory. Receipt verification and loaded-module
/// lookup happen in this backend before library initialization, independently of
/// the subsequently selected interpreter and actual runtime version.
pub trait NativeHintedBackendFactory {
    /// Actual library implementation, owned and exclusive from creation.
    type Library: NativeHintedLibraryBackend;
    /// Verify the exact original external prepared receipt and report actual
    /// loaded origins; no ambient search or version-only admission is permitted.
    fn dependency_evidence(
        &mut self,
        target: NativeHintedTarget,
    ) -> Result<NativeHintedDependencyEvidence, NativeHintedCaptureError>;
    /// Execute actual FT_Init_FreeType for an independent library owner.
    fn initialize_library(&mut self) -> Result<Self::Library, NativeHintedCaptureError>;
}
/// Original selected native hinted face with owned source, frozen request and
/// actual size/phase. `&mut self` enforces exclusive sequential slot execution.
pub struct NativeHintedFace<B: NativeHintedFaceBackend> {
    // Original destruction order: actual face/library before immutable source.
    backend: B,
    font: SfntFace,
    request: NativeHintedRequest,
    metrics: NativeHintedMetrics,
    glyph_count: u32,
    evidence: NativeHintedDependencyEvidence,
}
/// Create an actual face by executing the original producer admission/selection
/// order. No backend implementation is supplied implicitly by this crate.
pub fn create_native_hinted_face<F: NativeHintedBackendFactory>(
    factory: &mut F,
    font: SfntFace,
    request: &NativeHintedRequest,
) -> Result<
    NativeHintedFace<<F::Library as NativeHintedLibraryBackend>::Face>,
    NativeHintedCaptureError,
> {
    if font.data().is_empty()
        || font.data().len() > i64::MAX as usize
        || font.face_index() > u16::MAX as u32
        || request
            .pixels_per_em_26_6
            .iter()
            .any(|&v| v == 0 || v > i32::MAX as u32)
        || request.phase_26_6.iter().any(|&v| v >= 64)
    {
        return Err(NativeHintedCaptureError::InvalidArgument);
    }
    let target =
        NativeHintedTarget::current().ok_or(NativeHintedCaptureError::DependencyUnavailable)?;
    // Windows original native long is signed32, including memory-face byte length.
    if matches!(
        target,
        NativeHintedTarget::WindowsX64 | NativeHintedTarget::WindowsArm64
    ) && font.data().len() > i32::MAX as usize
    {
        return Err(NativeHintedCaptureError::InvalidArgument);
    }
    let evidence = factory.dependency_evidence(target)?;
    evidence.admit(target)?;
    let request = copy_request(request)?;
    let mut library = factory
        .initialize_library()
        .map_err(|_| NativeHintedCaptureError::ResourceExhausted)?;
    if library.version() != [2, 14, 3] || evidence.header_version != [2, 14, 3] {
        return Err(NativeHintedCaptureError::DependencyMismatch);
    }
    library
        .set_interpreter(request.hint_policy)
        .map_err(|_| NativeHintedCaptureError::DependencyMismatch)?;
    if library
        .interpreter()
        .map_err(|_| NativeHintedCaptureError::DependencyMismatch)?
        != request.hint_policy as u32
    {
        return Err(NativeHintedCaptureError::DependencyMismatch);
    }
    let mut backend = library
        .open_memory_face(font.clone())
        .map_err(|_| NativeHintedCaptureError::InvalidFont)?;
    let info = backend.information();
    if !info.scalable
        || info.color
        || !info.true_type_format
        || info.glyph_count <= 0
        || info.glyph_count > i64::from(u32::MAX)
    {
        return Err(NativeHintedCaptureError::UnsupportedFont);
    }
    if info.multiple_masters {
        let axes = backend.variation_axes().map_err(|error| {
            if error == NativeHintedCaptureError::ResourceExhausted {
                error
            } else {
                NativeHintedCaptureError::InvalidArgument
            }
        })?;
        if axes.is_empty()
            || axes.len() != request.variation_coordinates_16_16.len()
            || axes
                .iter()
                .zip(&request.variation_coordinates_16_16)
                .any(|(axis, &value)| {
                    i64::from(value) < axis.minimum || i64::from(value) > axis.maximum
                })
        {
            return Err(NativeHintedCaptureError::InvalidArgument);
        }
        backend
            .set_variations(&request.variation_coordinates_16_16)
            .map_err(|error| {
                if error == NativeHintedCaptureError::ResourceExhausted {
                    error
                } else {
                    NativeHintedCaptureError::InvalidArgument
                }
            })?;
    } else if !request.variation_coordinates_16_16.is_empty() {
        return Err(NativeHintedCaptureError::InvalidArgument);
    }
    let metrics = backend
        .request_nominal_size(request.pixels_per_em_26_6)
        .map_err(|error| {
            if error == NativeHintedCaptureError::InvalidFont {
                error
            } else {
                NativeHintedCaptureError::InvalidArgument
            }
        })?;
    if metrics.units_per_em == 0 || metrics.scale_16_16.iter().any(|&scale| scale <= 0) {
        return Err(NativeHintedCaptureError::InvalidFont);
    }
    backend.set_phase(request.phase_26_6);
    Ok(NativeHintedFace {
        backend,
        font,
        request,
        metrics,
        glyph_count: info.glyph_count as u32,
        evidence,
    })
}
impl<B: NativeHintedFaceBackend> NativeHintedFace<B> {
    /// Exact retained source bytes and selected collection face.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Original frozen request, independent of device metrics.
    pub fn request(&self) -> &NativeHintedRequest {
        &self.request
    }
    /// Actual original driver size frame.
    pub fn metrics(&self) -> NativeHintedMetrics {
        self.metrics
    }
    /// Actual backend-reported dependency origin and prepared receipt metadata.
    pub fn dependency_evidence(&self) -> &NativeHintedDependencyEvidence {
        &self.evidence
    }
    /// Capture every requested occurrence in order, with no glyph deduplication.
    /// Whole-ID admission precedes execution; failed later slots publish nothing.
    pub fn capture(
        &mut self,
        glyph_ids: &[u32],
    ) -> Result<Arc<NativeHintedBatch>, NativeHintedCaptureError> {
        if glyph_ids.iter().any(|&id| id >= self.glyph_count) {
            return Err(NativeHintedCaptureError::InvalidArgument);
        }
        let mut glyphs = Vec::new();
        glyphs
            .try_reserve_exact(glyph_ids.len())
            .map_err(|_| NativeHintedCaptureError::ResourceExhausted)?;
        let flags = NativeHintedLoadFlags::for_policy(self.request.outline_policy);
        for &glyph in glyph_ids {
            let captured = self.backend.capture_glyph(glyph, flags).map_err(|error| {
                if error == NativeHintedCaptureError::ResourceExhausted {
                    error
                } else {
                    NativeHintedCaptureError::HintingFailed
                }
            })?;
            if captured.glyph_id != glyph
                || captured.points_26_6.len() != captured.tags.len()
                || captured.points_26_6.len() > i16::MAX as usize
                || captured.contour_ends.len() > i16::MAX as usize
            {
                return Err(NativeHintedCaptureError::HintingFailed);
            }
            glyphs.push(captured);
        }
        let batch = NativeHintedBatch::from_capture(
            self.font.clone(),
            copy_request(&self.request)?,
            self.metrics,
            glyphs,
        )
        .map_err(|_| NativeHintedCaptureError::HintingFailed)?;
        Ok(Arc::new(batch))
    }
}

fn capture_slot(
    glyph: u32,
    slot: NativeHintedSlot<'_>,
) -> Result<NativeHintedGlyph, NativeHintedCaptureError> {
    if slot.glyph_id != glyph
        || !slot.outline_format
        || !slot.outline_check_succeeded
        || slot.points_26_6.len() != slot.tags.len()
        || slot.points_26_6.len() > i16::MAX as usize
        || slot.contour_ends.len() > i16::MAX as usize
    {
        return Err(NativeHintedCaptureError::HintingFailed);
    }
    let m = slot.metrics;
    Ok(NativeHintedGlyph {
        glyph_id: glyph,
        advance_26_6: m.advance_26_6,
        horizontal_bearing_26_6: m.horizontal_bearing_26_6,
        size_26_6: m.size_26_6,
        horizontal_advance_26_6: m.horizontal_advance_26_6,
        vertical_bearing_26_6: m.vertical_bearing_26_6,
        vertical_advance_26_6: m.vertical_advance_26_6,
        linear_advances_16_16: m.linear_advances_16_16,
        side_bearing_deltas_26_6: m.side_bearing_deltas_26_6,
        outline_flags: slot.outline_flags,
        points_26_6: copy(slot.points_26_6)?,
        tags: copy(slot.tags)?,
        contour_ends: copy(slot.contour_ends)?,
    })
}
