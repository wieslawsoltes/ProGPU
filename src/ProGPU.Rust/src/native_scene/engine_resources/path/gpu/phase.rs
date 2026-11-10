//! Exact native size grouping and split Boolean/signed phase ordering.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(super) enum Kernel {
    Single,
    Linear,
    Cubic,
    Ordinary,
    Signed,
    LinearSigned,
    Leaf,
    SignedLeaf,
    Rows,
    Boolean,
    Pack,
}
impl Kernel {
    pub(super) fn shader(self) -> (&'static str, &'static str) {
        use crate::shaders::*;
        match self {
            Self::Single => (PATH, "cs_main_single_path"),
            Self::Linear => (PATH, "cs_main_linear_path"),
            Self::Cubic => (PATH, "cs_main_cubic_path"),
            Self::Ordinary => (PATH, "cs_main_ordinary"),
            Self::Signed => (PATH, "cs_main"),
            Self::LinearSigned => (PATH, "cs_main_linear_winding"),
            Self::Leaf => (PATH, "cs_split_leaf"),
            Self::SignedLeaf => (PATH_SIGNED_LEAF, "cs_main"),
            Self::Rows => (PATH_SIGNED_ROWS, "cs_main"),
            Self::Boolean => (PATH, "cs_split_boolean_combine"),
            Self::Pack => (PATH_SIGNED_PACK, "cs_main"),
        }
    }
}
pub(super) struct Group {
    pub offset: u64,
    pub bytes: u64,
    pub size: [u32; 3],
}
pub(super) struct Phase {
    pub kernel: Kernel,
    pub bytes: Vec<u8>,
    pub groups: Vec<Group>,
    pub combine: bool,
    pub split_after: bool,
}
pub(super) fn plan(page: &impl NativeRasterPlan) -> Result<Vec<Phase>, Error> {
    let mut phases = Vec::new();
    let extent = |width: u32| {
        page.rasters().iter().fold([0, 0], |[x, y], r| {
            [x.max(r.width.div_ceil(width)), y.max(r.height.div_ceil(16))]
        })
    };
    if !page.uniforms().is_empty() {
        let kernel = if page.has_inline_signed_winding() {
            if page.has_only_lines() {
                Kernel::LinearSigned
            } else {
                Kernel::Signed
            }
        } else if page.has_inline_operations() {
            Kernel::Ordinary
        } else if page.has_only_lines() {
            Kernel::Linear
        } else if page.has_only_lines_or_cubics() {
            Kernel::Cubic
        } else {
            Kernel::Single
        };
        let grouped = !page.has_inline_operations()
            && page.split_leaf_uniforms().is_empty()
            && page.signed_leaf_uniforms().is_empty()
            && page.combine_uniforms().is_empty()
            && page.signed_combine_uniforms().is_empty();
        push(
            &mut phases,
            if grouped {
                grouped_phase(kernel, page.uniforms())?
            } else {
                phase(
                    kernel,
                    bytemuck::cast_slice(page.uniforms()),
                    page.uniforms().len(),
                    extent(64),
                    false,
                    false,
                )?
            },
        )?;
    }
    for uniforms in page.split_leaf_uniforms() {
        if !uniforms.is_empty() {
            push(
                &mut phases,
                phase(
                    Kernel::Leaf,
                    bytemuck::cast_slice(uniforms),
                    uniforms.len(),
                    extent(16),
                    false,
                    true,
                )?,
            )?;
        }
    }
    let signed_size = page
        .signed_leaf_uniforms()
        .iter()
        .flat_map(|p| p.iter())
        .fold([0, 0], |[x, y], u| {
            [
                x.max(u.width.div_ceil(16)),
                y.max((u.height * 8).div_ceil(16)),
            ]
        });
    for uniforms in page.signed_leaf_uniforms() {
        if !uniforms.is_empty() {
            push(
                &mut phases,
                phase(
                    Kernel::SignedLeaf,
                    bytemuck::cast_slice(uniforms),
                    uniforms.len(),
                    signed_size,
                    false,
                    false,
                )?,
            )?;
        }
    }
    for (uniforms, kernel, width) in [
        (page.signed_combine_uniforms(), Kernel::Rows, 16),
        (page.combine_uniforms(), Kernel::Boolean, 64),
        (page.signed_combine_uniforms(), Kernel::Pack, 64),
    ] {
        if !uniforms.is_empty() {
            let size = if kernel == Kernel::Boolean {
                extent(64)
            } else {
                uniforms.iter().fold([0, 0], |[x, y], u| {
                    [x.max(u.width.div_ceil(width)), y.max(u.height.div_ceil(16))]
                })
            };
            push(
                &mut phases,
                phase(
                    kernel,
                    bytemuck::cast_slice(uniforms),
                    uniforms.len(),
                    size,
                    true,
                    false,
                )?,
            )?;
        }
    }
    Ok(phases)
}
fn phase(
    kernel: Kernel,
    bytes: &[u8],
    count: usize,
    size: [u32; 2],
    combine: bool,
    split_after: bool,
) -> Result<Phase, Error> {
    let mut owned = allocate(bytes.len())?;
    owned.extend_from_slice(bytes);
    let groups = vec![Group {
        offset: 0,
        bytes: bytes.len() as u64,
        size: [size[0], size[1], count_u32(count)?],
    }];
    Ok(Phase {
        kernel,
        bytes: owned,
        groups,
        combine,
        split_after,
    })
}
fn grouped_phase(kernel: Kernel, uniforms: &[NativePathUniforms]) -> Result<Phase, Error> {
    if uniforms.len() > 1 << 20 || uniforms.iter().any(|u| u.path_op_kind != 0) {
        return Err(Error::InvalidInput);
    }
    let mut order = allocate(uniforms.len())?;
    order.extend(0..uniforms.len());
    order.sort_unstable_by_key(|&i| {
        (
            uniforms[i].width.div_ceil(64),
            uniforms[i].height.div_ceil(16),
            i,
        )
    });
    let mut bytes = Vec::new();
    let mut groups = Vec::new();
    let mut first = 0;
    while first < order.len() {
        let u = uniforms[order[first]];
        let size = [u.width.div_ceil(64), u.height.div_ceil(16)];
        let mut end = first + 1;
        while end < order.len()
            && end - first < 65535
            && uniforms[order[end]].width.div_ceil(64) == size[0]
            && uniforms[order[end]].height.div_ceil(16) == size[1]
        {
            end += 1;
        }
        let offset = bytes.len().checked_add(255).ok_or(Error::Capacity)? & !255;
        let length = (end - first) * 48;
        let next = offset.checked_add(length).ok_or(Error::Capacity)?;
        bytes
            .try_reserve(next - bytes.len())
            .map_err(|_| Error::Capacity)?;
        bytes.resize(next, 0);
        for (i, &index) in order[first..end].iter().enumerate() {
            bytes[offset + i * 48..offset + (i + 1) * 48]
                .copy_from_slice(bytemuck::bytes_of(&uniforms[index]));
        }
        push(
            &mut groups,
            Group {
                offset: offset as u64,
                bytes: length as u64,
                size: [size[0], size[1], (end - first) as u32],
            },
        )?;
        first = end;
    }
    Ok(Phase {
        kernel,
        bytes,
        groups,
        combine: false,
        split_after: false,
    })
}
