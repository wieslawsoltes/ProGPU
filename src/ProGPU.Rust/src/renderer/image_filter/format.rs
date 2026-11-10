//! Original filter result formats: canonical compute/render operations allocate
//! RGBA8; identity aliases and zero-radius whole-texture copies retain source.
use super::*;
impl ImageFilterPlan {
    pub(crate) fn texture_formats(
        &self,
        source: wgpu::TextureFormat,
    ) -> Result<Vec<wgpu::TextureFormat>, Error> {
        let mut formats = Vec::new();
        formats
            .try_reserve_exact(self.steps().len() + 1)
            .map_err(|_| Error::Capacity)?;
        formats.push(source);
        for step in self.steps() {
            let format = match &step.operation {
                Operation::Morphology(parameters) if parameters.iter().all(|p| p.radius == 0) => {
                    *formats
                        .get(*step.inputs.first().ok_or(Error::ResourceDomain)? as usize)
                        .ok_or(Error::ResourceDomain)?
                }
                _ => wgpu::TextureFormat::Rgba8Unorm,
            };
            formats.push(format);
        }
        Ok(formats)
    }
    pub(crate) fn output_texture_format(
        &self,
        source: wgpu::TextureFormat,
    ) -> Result<wgpu::TextureFormat, Error> {
        self.texture_formats(source)?
            .get(self.output() as usize)
            .copied()
            .ok_or(Error::ResourceDomain)
    }
}
