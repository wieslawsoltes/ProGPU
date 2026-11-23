//! Audited private C ABI boundary, translated from ProGPU's hinted_font.cpp.
//! Public C record declarations follow FreeType 2.14.3's documented API only;
//! no dependency implementation is copied. Runtime owns every pointed allocation.
use super::*;
use std::{
    ffi::{CStr, c_char, c_int, c_long, c_uint, c_ulong, c_void},
    marker::PhantomData,
    ptr::{self, NonNull},
    rc::Rc,
    sync::Arc,
};

#[repr(C)]
#[derive(Clone, Copy)]
struct Vector {
    x: c_long,
    y: c_long,
}
#[repr(C)]
struct Generic {
    data: *mut c_void,
    finalizer: Option<unsafe extern "C" fn(*mut c_void)>,
}
#[repr(C)]
struct BBox {
    x_min: c_long,
    y_min: c_long,
    x_max: c_long,
    y_max: c_long,
}
#[repr(C)]
struct FaceRec {
    num_faces: c_long,
    face_index: c_long,
    face_flags: c_long,
    style_flags: c_long,
    num_glyphs: c_long,
    family_name: *mut c_char,
    style_name: *mut c_char,
    num_fixed_sizes: c_int,
    available_sizes: *mut c_void,
    num_charmaps: c_int,
    charmaps: *mut c_void,
    generic: Generic,
    bbox: BBox,
    units_per_em: u16,
    ascender: i16,
    descender: i16,
    height: i16,
    max_advance_width: i16,
    max_advance_height: i16,
    underline_position: i16,
    underline_thickness: i16,
    glyph: *mut GlyphSlotRec,
    size: *mut SizeRec,
    charmap: *mut c_void,
    // Only the documented public prefix is accessed, never the private tail.
}
#[repr(C)]
struct SizeMetrics {
    x_ppem: u16,
    y_ppem: u16,
    x_scale: c_long,
    y_scale: c_long,
    ascender: c_long,
    descender: c_long,
    height: c_long,
    max_advance: c_long,
}
#[repr(C)]
struct SizeRec {
    face: *mut FaceRec,
    generic: Generic,
    metrics: SizeMetrics,
    internal: *mut c_void,
}
#[repr(C)]
struct GlyphMetrics {
    width: c_long,
    height: c_long,
    hori_bearing_x: c_long,
    hori_bearing_y: c_long,
    hori_advance: c_long,
    vert_bearing_x: c_long,
    vert_bearing_y: c_long,
    vert_advance: c_long,
}
#[repr(C)]
struct Bitmap {
    rows: c_uint,
    width: c_uint,
    pitch: c_int,
    buffer: *mut u8,
    num_grays: u16,
    pixel_mode: u8,
    palette_mode: u8,
    palette: *mut c_void,
}
#[repr(C)]
struct Outline {
    n_contours: i16,
    n_points: i16,
    points: *mut Vector,
    tags: *mut c_char,
    contours: *mut i16,
    flags: c_int,
}
#[repr(C)]
struct GlyphSlotRec {
    library: *mut c_void,
    face: *mut FaceRec,
    next: *mut GlyphSlotRec,
    glyph_index: c_uint,
    generic: Generic,
    metrics: GlyphMetrics,
    linear_hori_advance: c_long,
    linear_vert_advance: c_long,
    advance: Vector,
    format: c_uint,
    bitmap: Bitmap,
    bitmap_left: c_int,
    bitmap_top: c_int,
    outline: Outline,
    num_subglyphs: c_uint,
    subglyphs: *mut c_void,
    control_data: *mut c_void,
    control_len: c_long,
    lsb_delta: c_long,
    rsb_delta: c_long,
    // No private trailing field or bitmap payload is accessed.
}
#[repr(C)]
struct VariationAxisRec {
    name: *mut c_char,
    minimum: c_long,
    default: c_long,
    maximum: c_long,
    tag: c_ulong,
    string_id: c_uint,
}
#[repr(C)]
struct MultipleMasters {
    axis_count: c_uint,
    design_count: c_uint,
    named_style_count: c_uint,
    axes: *mut VariationAxisRec,
    named_styles: *mut c_void,
}
#[repr(C)]
struct SizeRequest {
    kind: c_int,
    width: c_long,
    height: c_long,
    horizontal_resolution: c_uint,
    vertical_resolution: c_uint,
}
#[repr(C)]
struct Matrix {
    xx: c_long,
    xy: c_long,
    yx: c_long,
    yy: c_long,
}

unsafe extern "C" {
    fn FT_Init_FreeType(library: *mut *mut c_void) -> c_int;
    fn FT_Done_FreeType(library: *mut c_void) -> c_int;
    fn FT_Library_Version(
        library: *mut c_void,
        major: *mut c_int,
        minor: *mut c_int,
        patch: *mut c_int,
    );
    fn FT_Property_Set(
        library: *mut c_void,
        module: *const c_char,
        property: *const c_char,
        value: *const c_void,
    ) -> c_int;
    fn FT_Property_Get(
        library: *mut c_void,
        module: *const c_char,
        property: *const c_char,
        value: *mut c_void,
    ) -> c_int;
    fn FT_New_Memory_Face(
        library: *mut c_void,
        bytes: *const u8,
        length: c_long,
        face_index: c_long,
        face: *mut *mut FaceRec,
    ) -> c_int;
    fn FT_Done_Face(face: *mut FaceRec) -> c_int;
    fn FT_Get_Font_Format(face: *mut FaceRec) -> *const c_char;
    fn FT_Get_MM_Var(face: *mut FaceRec, axes: *mut *mut MultipleMasters) -> c_int;
    fn FT_Done_MM_Var(library: *mut c_void, axes: *mut MultipleMasters) -> c_int;
    fn FT_Set_Var_Design_Coordinates(
        face: *mut FaceRec,
        count: c_uint,
        coordinates: *mut c_long,
    ) -> c_int;
    fn FT_Request_Size(face: *mut FaceRec, request: *mut SizeRequest) -> c_int;
    fn FT_Set_Transform(face: *mut FaceRec, matrix: *mut Matrix, delta: *mut Vector);
    fn FT_Load_Glyph(face: *mut FaceRec, glyph: c_uint, flags: i32) -> c_int;
    fn FT_Outline_Check(outline: *mut Outline) -> c_int;
    fn FT_Get_Char_Index(face: *mut FaceRec, character: c_ulong) -> c_uint;
    fn FT_MulFix(value: c_long, scale: c_long) -> c_long;
}

#[cfg(any(target_os = "linux", target_os = "macos"))]
#[repr(C)]
struct DlInfo {
    filename: *const c_char,
    base: *mut c_void,
    symbol: *const c_char,
    symbol_address: *mut c_void,
}
#[cfg(any(target_os = "linux", target_os = "macos"))]
#[cfg_attr(target_os = "linux", link(name = "dl"))]
unsafe extern "C" {
    fn dladdr(address: *const c_void, information: *mut DlInfo) -> c_int;
}
#[cfg(target_os = "windows")]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetModuleHandleExW(flags: u32, address: *const u16, module: *mut *mut c_void) -> i32;
}

fn module_of(address: *const c_void) -> usize {
    #[cfg(target_os = "windows")]
    {
        let mut module = ptr::null_mut();
        // SAFETY: FROM_ADDRESS treats the pointer as an address, not a string.
        // UNCHANGED_REFCOUNT obtains an observation, not a new lifetime owner.
        let status = unsafe { GetModuleHandleExW(0x4 | 0x2, address.cast(), &mut module) };
        if status != 0 { module as usize } else { 0 }
    }
    #[cfg(any(target_os = "linux", target_os = "macos"))]
    {
        let mut info = DlInfo {
            filename: ptr::null(),
            base: ptr::null_mut(),
            symbol: ptr::null(),
            symbol_address: ptr::null_mut(),
        };
        // SAFETY: address is a linked function address; info is writable ABI storage.
        if unsafe { dladdr(address, &mut info) } != 0 {
            info.base as usize
        } else {
            0
        }
    }
}
#[inline(never)]
fn image_marker() {}
/// Observe all original public symbols in their actual loaded module. Static
/// receipt identity alone does not authorize an ambient/shared implementation.
pub fn dependency_evidence() -> Result<DependencyEvidence> {
    let producer = module_of(image_marker as *const () as *const c_void);
    let addresses = [
        FT_Init_FreeType as *const (),
        FT_Done_FreeType as *const (),
        FT_Library_Version as *const (),
        FT_Property_Set as *const (),
        FT_Property_Get as *const (),
        FT_New_Memory_Face as *const (),
        FT_Done_Face as *const (),
        FT_Get_Font_Format as *const (),
        FT_Get_MM_Var as *const (),
        FT_Done_MM_Var as *const (),
        FT_Set_Var_Design_Coordinates as *const (),
        FT_Request_Size as *const (),
        FT_Set_Transform as *const (),
        FT_Load_Glyph as *const (),
        FT_Outline_Check as *const (),
        FT_Get_Char_Index as *const (),
        FT_MulFix as *const (),
    ];
    let functions = addresses.map(|p| module_of(p.cast()));
    if producer == 0 || functions.iter().any(|&owner| owner != producer) {
        return Err(Error::DependencyMismatch);
    }
    Ok(DependencyEvidence {
        receipt: BUILD_RECEIPT,
        producer_image: producer,
        function_images: functions,
    })
}
fn ft_status(status: c_int) -> Result<()> {
    if status == 0 {
        Ok(())
    } else {
        Err(Error::FreeType(status))
    }
}
fn allocate<T>(count: usize) -> Result<Vec<T>> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| Error::ResourceExhausted)?;
    Ok(values)
}

/// One original FT_Init_FreeType owner. It is deliberately neither Send nor Sync:
/// all callbacks and mutable native state stay on the owning capture thread.
pub struct Library {
    raw: NonNull<c_void>,
    _thread: PhantomData<Rc<()>>,
}
impl std::fmt::Debug for Library {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FreeTypeLibrary").finish_non_exhaustive()
    }
}
impl Library {
    pub fn new() -> Result<Self> {
        dependency_evidence()?;
        let mut raw = ptr::null_mut();
        // SAFETY: writable out-pointer; successful ownership is acquired once.
        let status = unsafe { FT_Init_FreeType(&mut raw) };
        if status != 0 {
            if !raw.is_null() {
                // SAFETY: any failure-owned nonnull initialized handle retires here.
                unsafe {
                    FT_Done_FreeType(raw);
                }
            }
            return Err(Error::ResourceExhausted);
        }
        let value = Self {
            raw: NonNull::new(raw).ok_or(Error::ResourceExhausted)?,
            _thread: PhantomData,
        };
        Ok(value)
    }
    pub fn version(&self) -> [u32; 3] {
        let (mut major, mut minor, mut patch) = (0, 0, 0);
        // SAFETY: this live exclusive library owns all three writable integers.
        unsafe {
            FT_Library_Version(self.raw.as_ptr(), &mut major, &mut minor, &mut patch);
        }
        [major as u32, minor as u32, patch as u32]
    }
    pub fn set_interpreter(&mut self, value: Interpreter) -> Result<()> {
        let value = value as c_uint;
        // SAFETY: exact property requires a pointer to one native unsigned int;
        // both names have static NUL-terminated storage and no pointer escapes.
        ft_status(unsafe {
            FT_Property_Set(
                self.raw.as_ptr(),
                c"truetype".as_ptr(),
                c"interpreter-version".as_ptr(),
                ptr::from_ref(&value).cast(),
            )
        })
    }
    pub fn interpreter(&self) -> Result<u32> {
        let mut actual = 0u32;
        // SAFETY: exact property writes one native unsigned int into owned storage.
        ft_status(unsafe {
            FT_Property_Get(
                self.raw.as_ptr(),
                c"truetype".as_ptr(),
                c"interpreter-version".as_ptr(),
                ptr::from_mut(&mut actual).cast(),
            )
        })?;
        Ok(actual)
    }
    /// Consume the library and retain the exact immutable memory bytes. The
    /// returned face retires before its library, which retires before the bytes.
    pub fn open_memory_face(self, bytes: Arc<[u8]>, face_index: u32) -> Result<Face> {
        // Opaque library construction itself preserves the original init-only
        // error stage. Check the pinned public-record ABI before creating/accessing
        // a face, including callers outside ProGPU's already-checked factory.
        let version = self.version();
        if version != BUILD_RECEIPT.header_version || version != [2, 14, 3] {
            return Err(Error::DependencyMismatch);
        }
        let length = c_long::try_from(bytes.len()).map_err(|_| Error::InvalidArgument)?;
        if bytes.is_empty() || face_index > 0xffff {
            return Err(Error::InvalidArgument);
        }
        let mut value = Face {
            raw: ptr::null_mut(),
            library: self,
            bytes,
            axes: Vec::new(),
            last: None,
        };
        // SAFETY: Arc slice storage cannot move or mutate. value retains it until
        // after both native owners retire, including every failure path below.
        let status = unsafe {
            FT_New_Memory_Face(
                value.library.raw.as_ptr(),
                value.bytes.as_ptr(),
                length,
                face_index as c_long,
                &mut value.raw,
            )
        };
        if status != 0 || value.raw.is_null() {
            return Err(Error::InvalidFont);
        }
        Ok(value)
    }
}
impl Drop for Library {
    fn drop(&mut self) {
        // SAFETY: this unique non-Clone owner is live; all owned faces retired first.
        unsafe {
            FT_Done_FreeType(self.raw.as_ptr());
        }
    }
}

/// Exclusive actual memory face. Raw handles and borrowed driver pointers never
/// cross its public API; only immutable owned snapshots leave a load operation.
pub struct Face {
    raw: *mut FaceRec,
    library: Library,
    bytes: Arc<[u8]>,
    axes: Vec<VariationAxis>,
    last: Option<CapturedGlyph>,
}
impl std::fmt::Debug for Face {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FreeTypeFace")
            .field("source_bytes", &self.bytes.len())
            .finish_non_exhaustive()
    }
}
impl Drop for Face {
    fn drop(&mut self) {
        if !self.raw.is_null() {
            // SAFETY: failure or success handle belongs to this exclusive owner.
            // Rust subsequently drops library before immutable memory bytes.
            unsafe {
                FT_Done_Face(self.raw);
            }
        }
    }
}
struct MmOwner {
    library: *mut c_void,
    axes: *mut MultipleMasters,
}
impl Drop for MmOwner {
    fn drop(&mut self) {
        if !self.axes.is_null() {
            // SAFETY: FT_Get_MM_Var allocated this descriptor from this library.
            unsafe {
                FT_Done_MM_Var(self.library, self.axes);
            }
        }
    }
}
impl Face {
    fn record(&self) -> &FaceRec {
        // SAFETY: only a successful nonnull native face is publicly constructed;
        // its documented prefix remains valid until this owner's Drop.
        unsafe { &*self.raw }
    }
    pub fn information(&self) -> FaceInformation {
        let face = self.record();
        // SAFETY: the live face returns a library-owned static format string.
        let format = unsafe { FT_Get_Font_Format(self.raw) };
        let true_type_format =
            !format.is_null() && unsafe { CStr::from_ptr(format) }.to_bytes() == b"TrueType";
        FaceInformation {
            true_type_format,
            scalable: face.face_flags & 1 != 0,
            color: face.face_flags & (1 << 14) != 0,
            glyph_count: face.num_glyphs as i64,
            multiple_masters: face.face_flags & (1 << 8) != 0,
        }
    }
    pub fn variation_axes(&mut self) -> Result<&[VariationAxis]> {
        let mut owner = MmOwner {
            library: self.library.raw.as_ptr(),
            axes: ptr::null_mut(),
        };
        // SAFETY: live face and writable owned descriptor out-pointer. The guard
        // retires any returned allocation even on native error or Rust unwind.
        ft_status(unsafe { FT_Get_MM_Var(self.raw, &mut owner.axes) })?;
        if owner.axes.is_null() {
            return Err(Error::InvalidArgument);
        }
        // SAFETY: successful FT_Get_MM_Var publishes its documented descriptor.
        let descriptor = unsafe { &*owner.axes };
        if descriptor.axis_count == 0 || descriptor.axes.is_null() {
            return Err(Error::InvalidArgument);
        }
        let count = descriptor.axis_count as usize;
        if count > isize::MAX as usize / size_of::<VariationAxisRec>() {
            return Err(Error::InvalidArgument);
        }
        let mut axes = allocate(count)?;
        // SAFETY: count and pointer were checked; FreeType owns this exact axis
        // allocation until the guard retires after the full snapshot is copied.
        for axis in unsafe { std::slice::from_raw_parts(descriptor.axes, count) } {
            axes.push(VariationAxis {
                minimum: axis.minimum as i64,
                default: axis.default as i64,
                maximum: axis.maximum as i64,
            });
        }
        self.axes = axes;
        Ok(&self.axes)
    }
    pub fn set_variations(&mut self, coordinates: &[i32]) -> Result<()> {
        if !self.information().multiple_masters {
            return if coordinates.is_empty() {
                Ok(())
            } else {
                Err(Error::InvalidArgument)
            };
        }
        if self.axes.is_empty() {
            self.variation_axes()?;
        }
        if coordinates.len() != self.axes.len()
            || coordinates
                .iter()
                .zip(&self.axes)
                .any(|(&v, a)| i64::from(v) < a.minimum || i64::from(v) > a.maximum)
        {
            return Err(Error::InvalidArgument);
        }
        let count = c_uint::try_from(coordinates.len()).map_err(|_| Error::InvalidArgument)?;
        let mut native = allocate(coordinates.len())?;
        native.extend(coordinates.iter().map(|&v| c_long::from(v)));
        // SAFETY: the complete axis-order native fixed array is valid for this
        // synchronous call. No pointer is retained by FreeType after selection.
        ft_status(unsafe { FT_Set_Var_Design_Coordinates(self.raw, count, native.as_mut_ptr()) })
    }
    pub fn request_nominal_size(&mut self, pixels: [u32; 2]) -> Result<DeviceMetrics> {
        if pixels.iter().any(|&v| v == 0 || v > i32::MAX as u32) {
            return Err(Error::InvalidArgument);
        }
        let mut request = SizeRequest {
            kind: 0,
            width: pixels[0] as c_long,
            height: pixels[1] as c_long,
            horizontal_resolution: 0,
            vertical_resolution: 0,
        };
        // SAFETY: exact NOMINAL request, unchanged 26.6 sizes and zero resolutions.
        ft_status(unsafe { FT_Request_Size(self.raw, &mut request) })?;
        let face = self.record();
        if face.size.is_null() || face.units_per_em == 0 {
            return Err(Error::InvalidFont);
        }
        // SAFETY: the live face owns its active FT_Size; no concurrent load/mutation.
        let size = unsafe { &(*face.size).metrics };
        if size.x_scale <= 0 || size.y_scale <= 0 {
            return Err(Error::InvalidFont);
        }
        Ok(DeviceMetrics {
            units_per_em: face.units_per_em,
            pixels_per_em: [size.x_ppem.into(), size.y_ppem.into()],
            scale_16_16: [size.x_scale as i64, size.y_scale as i64],
            driver_line_metrics_26_6: [
                size.ascender as i64,
                size.descender as i64,
                size.height as i64,
                size.max_advance as i64,
            ],
            design_line_metrics: [
                face.ascender.into(),
                face.descender.into(),
                face.height.into(),
                face.max_advance_width.into(),
            ],
        })
    }
    pub fn set_phase(&mut self, phase: [u32; 2]) -> Result<()> {
        if phase.iter().any(|&v| v >= 64) {
            return Err(Error::InvalidArgument);
        }
        let mut delta = Vector {
            x: phase[0] as c_long,
            y: phase[1] as c_long,
        };
        // SAFETY: exact original null matrix resets the driver transform;
        // the bounded phase vector remains valid throughout this synchronous call.
        unsafe {
            FT_Set_Transform(self.raw, ptr::null_mut(), &mut delta);
        }
        Ok(())
    }
    pub fn load_glyph(&mut self, glyph: u32, target: OutlineTarget) -> Result<&CapturedGlyph> {
        let candidate = self.capture_glyph(glyph, target)?;
        self.last = Some(candidate);
        self.last.as_ref().ok_or(Error::HintingFailed)
    }
    /// Transfer one checked immediate snapshot without copying its arrays again.
    /// The native slot remains exclusive and no borrowed pointer is published.
    pub fn capture_glyph(&mut self, glyph: u32, target: OutlineTarget) -> Result<CapturedGlyph> {
        if i64::from(glyph) >= self.record().num_glyphs as i64 {
            return Err(Error::InvalidArgument);
        }
        // SAFETY: exclusive face, admitted ID and exact original outline-only flags.
        ft_status(unsafe { FT_Load_Glyph(self.raw, glyph, target.load_flags()) })?;
        let slot = self.record().glyph;
        if slot.is_null() {
            return Err(Error::HintingFailed);
        }
        // SAFETY: FT_Load_Glyph succeeded; its face-owned slot is live and immutable
        // until this exclusive method finishes copying it. FreeType provides each
        // public field in the pinned header ABI and all corresponding allocations.
        let source = unsafe { &*slot };
        let outline = &source.outline;
        if source.glyph_index != glyph
            || source.format != u32::from_be_bytes(*b"outl")
            || outline.n_points < 0
            || outline.n_contours < 0
            || (outline.n_points > 0 && (outline.points.is_null() || outline.tags.is_null()))
            || (outline.n_contours > 0 && outline.contours.is_null())
        {
            return Err(Error::HintingFailed);
        }
        // SAFETY: FT_Outline_Check only checks the actual borrowed descriptor;
        // it does not mutate the slot or its arrays, despite its historical mut pointer.
        ft_status(unsafe { FT_Outline_Check(ptr::addr_of!((*slot).outline).cast_mut()) })?;
        let n = outline.n_points as usize;
        let c = outline.n_contours as usize;
        let mut points = allocate(n)?;
        let mut tags = allocate(n)?;
        let mut contours = allocate(c)?;
        if n != 0 {
            // SAFETY: positive bounded native counts and corresponding pointers
            // were admitted above; the checked outline owns both exact arrays.
            let positions = unsafe { std::slice::from_raw_parts(outline.points, n) };
            let source_tags = unsafe { std::slice::from_raw_parts(outline.tags.cast::<u8>(), n) };
            points.extend(positions.iter().map(|p| [p.x as i64, p.y as i64]));
            tags.extend_from_slice(source_tags);
        }
        if c != 0 {
            // SAFETY: positive bounded contour count and checked source allocation.
            contours.extend_from_slice(unsafe { std::slice::from_raw_parts(outline.contours, c) });
        }
        let m = &source.metrics;
        let captured = CapturedGlyph {
            glyph_id: glyph,
            metrics: SlotMetrics {
                advance_26_6: [source.advance.x as i64, source.advance.y as i64],
                horizontal_bearing_26_6: [m.hori_bearing_x as i64, m.hori_bearing_y as i64],
                size_26_6: [m.width as i64, m.height as i64],
                horizontal_advance_26_6: m.hori_advance as i64,
                vertical_bearing_26_6: [m.vert_bearing_x as i64, m.vert_bearing_y as i64],
                vertical_advance_26_6: m.vert_advance as i64,
                linear_advances_16_16: [
                    source.linear_hori_advance as i64,
                    source.linear_vert_advance as i64,
                ],
                side_bearing_deltas_26_6: [source.lsb_delta as i64, source.rsb_delta as i64],
            },
            outline_flags: outline.flags,
            points_26_6: points,
            tags,
            contour_ends: contours,
        };
        Ok(captured)
    }
    /// Original nominal character mapping from this actual selected face.
    pub fn character_index(&mut self, scalar: u32) -> u32 {
        // SAFETY: live exclusive face and a representable native unsigned long.
        unsafe { FT_Get_Char_Index(self.raw, scalar.into()) }
    }
    /// Actual dependency fixed multiplication, preserving native long admission.
    pub fn multiply_fixed(&self, value: i64, scale: i64) -> Result<i64> {
        let value = c_long::try_from(value).map_err(|_| Error::InvalidArgument)?;
        let scale = c_long::try_from(scale).map_err(|_| Error::InvalidArgument)?;
        // SAFETY: scalar public arithmetic has no pointer or owner preconditions.
        Ok(unsafe { FT_MulFix(value, scale) } as i64)
    }
}
