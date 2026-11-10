use super::*;
use crate::image::{
    ImageContext, ImageResourceError, RasterExecution, RasterSnapshot, TextureImage,
};
use crate::image_filter::{
    FilterPlanRequest, ImageFilter, ImageFilterPlan, ImageFilteredImageDraw,
};
use crate::{Scene, image::ImageDraw};
impl Shader {
    /// Original public image shader factory makes a real owned base-level copy.
    /// Source callbacks retain the original submitted copy lease until completion.
    /// Requested mip reconstruction uses the canonical mip producer on that copy.
    pub async fn image(
        image: &TextureImage,
        tile: [ShaderTileMode; 2],
        sampling: ShaderSampling,
        matrix: CanvasMatrix,
        is_raw: bool,
    ) -> Result<Self, ImageResourceError> {
        let copy = if sampling.mode.uses_mipmaps() || sampling.anisotropy > 1 {
            image.to_texture_image(image.context(), true).await?
        } else {
            image.owned_copy()?
        };
        Ok(Self::retained_image(
            FilterImageSource {
                image: copy.gpu_image().into(),
                color_space: copy
                    .info()
                    .color_space
                    .as_ref()
                    .map(ColorSpace::to_filter_color_space),
            },
            tile,
            sampling,
            matrix,
            is_raw,
        )?)
    }
    /// Original bitmap producer owns its actual uploaded immutable pixel snapshot.
    /// No CPU procedural shader painting or foreign image renderer is selected.
    pub async fn bitmap(
        context: &ImageContext,
        pixels: &RasterSnapshot,
        execution: RasterExecution,
        tile: [ShaderTileMode; 2],
        sampling: ShaderSampling,
        matrix: CanvasMatrix,
    ) -> Result<Self, ImageResourceError> {
        let image = TextureImage::from_raster(context, pixels, execution)?;
        let image = if sampling.mode.uses_mipmaps() || sampling.anisotropy > 1 {
            image.to_texture_image(context, true).await?
        } else {
            image
        };
        Ok(Self::retained_image(
            FilterImageSource {
                image: image.gpu_image().into(),
                color_space: image
                    .info()
                    .color_space
                    .as_ref()
                    .map(ColorSpace::to_filter_color_space),
            },
            tile,
            sampling,
            matrix,
            false,
        )?)
    }
    /// Prepare the canonical complete source graph over an actual transparent
    /// canvas target. The blank capture is genuine initialized GPU storage;
    /// source leaves retain and execute their own image/picture recordings.
    pub async fn prepare_scene(
        &self,
        context: &ImageContext,
        size: [u32; 2],
        filter_transform: CanvasMatrix,
    ) -> Result<Scene, ImageResourceError> {
        self.0.source.preflight_source_devices(context.device())?;
        let limit = context.device().limits().max_texture_dimension_2d;
        if size.contains(&0) || size.iter().any(|v| *v > limit || *v > i32::MAX as u32) {
            return Err(Error::Capacity.into());
        }
        filter_transform.affine()?;
        let filter = ImageFilter::shader(Some(self.source()), false, None)?;
        let request =
            FilterPlanRequest::new(size, size, FilterTransform::new(filter_transform.matrix4()));
        let plan = ImageFilterPlan::new(&filter, request)?;
        if plan
            .steps()
            .iter()
            .any(|s| s.operation.is_explicitly_unsupported())
        {
            return Err(Error::UnsupportedExecution.into());
        }
        let blank = TextureImage::from_picture(
            context,
            Arc::new(Scene::default()),
            size,
            Matrix4::IDENTITY,
        )
        .await?;
        let draw = ImageDraw::new(
            blank.gpu_image(),
            [0.0, 0.0, size[0] as f32, size[1] as f32],
        );
        let mut filtered = ImageFilteredImageDraw::new(draw, filter);
        filtered.request = request;
        let mut scene = Scene::default();
        scene.draw_image_filtered_image(filtered)?;
        Ok(scene)
    }
    /// Materialize one immutable original shader result against its actual context.
    /// Reusing the returned texture performs no shader/source callbacks at encode.
    pub async fn render(
        &self,
        context: &ImageContext,
        size: [u32; 2],
        filter_transform: CanvasMatrix,
    ) -> Result<TextureImage, ImageResourceError> {
        let scene = self.prepare_scene(context, size, filter_transform).await?;
        TextureImage::from_picture(context, Arc::new(scene), size, Matrix4::IDENTITY).await
    }
}
