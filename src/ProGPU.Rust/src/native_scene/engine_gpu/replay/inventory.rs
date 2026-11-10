use super::*;
impl NativePreparedReplayResources {
    /// Complete operation/resource ownership admission before encoding begins.
    pub fn admit(
        &self,
        root: &NativeRenderAttachment,
        rgb: &[Option<PreparedNativeRgbGlyphPage>],
        skipped: &[bool],
    ) -> Result<(), Error> {
        let operations = self.source.operations();
        let count = operations.len();
        if [
            self.bundles.len(),
            self.layer_draws.len(),
            self.backdrops.len(),
            self.shaders.len(),
            self.effect_indices.len(),
            self.advanced_indices.len(),
            self.rgb_pages.len(),
            rgb.len(),
            skipped.len(),
        ]
        .into_iter()
        .any(|n| n != count)
            || !Arc::ptr_eq(&self.source, self.frames.source())
            || !Arc::ptr_eq(self.source.source(), self.pages.source().source())
            || !Arc::ptr_eq(self.source.targets(), self.frames.targets().plan())
            || root.device() != self.frames.targets().device()
            || root.format().texture_format() != self.frames.targets().format()
            || root.size()
                != [
                    self.source.source().frame().width,
                    self.source.source().frame().height,
                ]
            || self.pages.material().device() != root.device()
        {
            return Err(Error::ResourceDomain);
        }
        match (&self.composite, self.source.vertices().is_empty()) {
            (None, true) => {}
            (Some(vertices), false)
                if vertices.device() == root.device()
                    && bytemuck::cast_slice::<_, u8>(vertices.vertices())
                        == bytemuck::cast_slice::<_, u8>(self.source.vertices()) => {}
            _ => return Err(Error::ResourceDomain),
        }
        match (&self.root_copy, self.source.root_copy_vertex()) {
            (None, None) => {}
            (Some(copy), Some(_))
                if copy.destination().device() == root.device()
                    && copy.destination().texture() == root.texture()
                    && copy.destination().view() == root.view() => {}
            _ => return Err(Error::ResourceDomain),
        }
        if self.source.source().budget().destination_sampling != self.root_copy.is_some() {
            return Err(Error::ResourceDomain);
        }
        for mask in &self.masks {
            if !Arc::ptr_eq(mask.request().scene(), self.source.source().scene())
                || mask.binding().device() != root.device()
            {
                return Err(Error::ResourceDomain);
            }
        }
        if self
            .effects
            .as_ref()
            .is_some_and(|p| p.device() != root.device())
            || self.advanced.as_ref().is_some_and(|p| {
                p.device() != root.device() || p.target_format() != root.format().texture_format()
            })
        {
            return Err(Error::ResourceDomain);
        }
        for (i, operation) in operations.iter().enumerate() {
            let effect = match operation {
                NativeReplayOperation::PushLayer(p) if p.backdrop => p.effect.as_ref(),
                NativeReplayOperation::PopLayer(p) if !p.backdrop => p.effect.as_ref(),
                _ => None,
            };
            match (effect, self.effect_indices[i], self.shaders[i].as_ref()) {
                (None, None, None) => {}
                (Some(NativeReplayEffect::Builtin(plan)), Some(index), None) => {
                    let prepared = self
                        .effects
                        .as_ref()
                        .and_then(|p| p.request(index))
                        .ok_or(Error::ResourceDomain)?;
                    let target = match operation {
                        NativeReplayOperation::PushLayer(p) => p.target,
                        NativeReplayOperation::PopLayer(p) => p.source,
                        _ => unreachable!(),
                    };
                    let actual = self
                        .frames
                        .targets()
                        .target(target)
                        .ok_or(Error::ResourceDomain)?;
                    if !Arc::ptr_eq(&prepared.plan, plan) || !Arc::ptr_eq(&prepared.target, actual)
                    {
                        return Err(Error::ResourceDomain);
                    }
                }
                (Some(NativeReplayEffect::Shader(plan)), None, Some(prepared)) => {
                    if !Arc::ptr_eq(prepared.plan().replay(), plan) {
                        return Err(Error::ResourceDomain);
                    }
                    if let Some(mask) = prepared.mask() {
                        self.require_mask(mask)?;
                    }
                }
                _ => return Err(Error::ResourceDomain),
            }
            match operation {
                NativeReplayOperation::Bundle(span) => {
                    let prepared = self.bundles[i].as_ref().ok_or(Error::ResourceDomain)?;
                    if !std::ptr::eq(prepared.source(), span)
                        || !Arc::ptr_eq(prepared.pages(), &self.pages)
                    {
                        return Err(Error::ResourceDomain);
                    }
                    if let Some(mask) = prepared.mask() {
                        self.require_mask(mask)?;
                    }
                    if self.layer_draws[i].is_some()
                        || self.backdrops[i].is_some()
                        || self.advanced_indices[i].is_some()
                        || rgb[i].is_some()
                    {
                        return Err(Error::ResourceDomain);
                    }
                }
                NativeReplayOperation::PushLayer(push) => {
                    if self.bundles[i].is_some()
                        || self.layer_draws[i].is_some()
                        || self.advanced_indices[i].is_some()
                        || rgb[i].is_some()
                    {
                        return Err(Error::ResourceDomain);
                    }
                    match (&self.backdrops[i], push.backdrop) {
                        (None, false) => {}
                        (Some(prepared), true) if std::ptr::eq(prepared.source(), push) => {}
                        _ => return Err(Error::ResourceDomain),
                    }
                    let Some(NativeReplayOperation::PopLayer(pop)) =
                        operations.get(push.pop_operation as usize)
                    else {
                        return Err(Error::InvalidInput);
                    };
                    if pop.push_operation as usize != i
                        || pop.occurrence != push.occurrence
                        || pop.source != push.target
                        || pop.target != push.parent
                    {
                        return Err(Error::InvalidInput);
                    }
                }
                NativeReplayOperation::PopLayer(pop) => {
                    if self.bundles[i].is_some() || self.backdrops[i].is_some() || rgb[i].is_some()
                    {
                        return Err(Error::ResourceDomain);
                    }
                    let final_sample = pop
                        .effect
                        .as_ref()
                        .is_some_and(NativeReplayEffect::final_sample);
                    if final_sample {
                        if self.layer_draws[i].is_some() || self.advanced_indices[i].is_some() {
                            return Err(Error::ResourceDomain);
                        }
                    } else if pop.composite.advanced.is_some() {
                        if self.layer_draws[i].is_some() {
                            return Err(Error::ResourceDomain);
                        }
                        if pop.composite.drawable {
                            let index = self.advanced_indices[i].ok_or(Error::ResourceDomain)?;
                            let request = self
                                .advanced
                                .as_ref()
                                .and_then(|p| p.request(index))
                                .ok_or(Error::ResourceDomain)?;
                            let source = self
                                .frames
                                .targets()
                                .target(pop.source)
                                .ok_or(Error::ResourceDomain)?;
                            let parent = self
                                .frames
                                .targets()
                                .target(pop.target)
                                .ok_or(Error::ResourceDomain)?;
                            if !Arc::ptr_eq(&request.source, source)
                                || !Arc::ptr_eq(&request.parent, parent)
                            {
                                return Err(Error::ResourceDomain);
                            }
                        } else if self.advanced_indices[i].is_some() {
                            return Err(Error::ResourceDomain);
                        }
                    } else {
                        if self.advanced_indices[i].is_some() {
                            return Err(Error::ResourceDomain);
                        }
                        match (&self.layer_draws[i], pop.composite.drawable) {
                            (None, false) => {}
                            (Some(prepared), true) if std::ptr::eq(prepared.source(), pop) => {
                                if let Some(mask) = prepared.mask() {
                                    self.require_mask(mask)?;
                                }
                            }
                            _ => return Err(Error::ResourceDomain),
                        }
                    }
                }
                NativeReplayOperation::Clear { .. } => {
                    if self.bundles[i].is_some()
                        || self.layer_draws[i].is_some()
                        || self.backdrops[i].is_some()
                        || self.advanced_indices[i].is_some()
                        || rgb[i].is_some()
                    {
                        return Err(Error::ResourceDomain);
                    }
                }
                NativeReplayOperation::Rgb { draw, .. } => {
                    if self.bundles[i].is_some()
                        || self.layer_draws[i].is_some()
                        || self.backdrops[i].is_some()
                        || self.advanced_indices[i].is_some()
                    {
                        return Err(Error::ResourceDomain);
                    }
                    if let Some(draw) = draw {
                        let page = self.rgb_pages[i].as_ref().ok_or(Error::ResourceDomain)?;
                        let frame = self
                            .source
                            .source()
                            .draws()
                            .rgb_frames()
                            .get(draw.page_index as usize)
                            .ok_or(Error::ResourceDomain)?;
                        if !same_frame(page.frame(), frame)
                            || page.actual_target_size() != draw.allocation.size
                            || page.scissor() != draw.scissor
                        {
                            return Err(Error::ResourceDomain);
                        }
                    } else if self.rgb_pages[i].is_some() {
                        return Err(Error::ResourceDomain);
                    }
                    match (draw, rgb[i].as_ref()) {
                        (None, None) => {}
                        (Some(_), None) if skipped[i] => {}
                        (Some(draw), Some(prepared)) => {
                            let source = self.rgb_pages[i].as_ref().ok_or(Error::ResourceDomain)?;
                            if !Arc::ptr_eq(source, prepared.page())
                                || prepared.device() != root.device()
                                || prepared.page().actual_target_size() != draw.allocation.size
                                || prepared.page().scissor() != draw.scissor
                            {
                                return Err(Error::ResourceDomain);
                            }
                            if let Some(request) = &draw.mask {
                                if !self.masks.iter().any(|m| Arc::ptr_eq(m.request(), request)) {
                                    return Err(Error::ResourceDomain);
                                }
                            }
                        }
                        _ => return Err(Error::ResourceDomain),
                    }
                }
            }
            if !matches!(operation, NativeReplayOperation::Rgb { .. })
                && self.rgb_pages[i].is_some()
            {
                return Err(Error::ResourceDomain);
            }
        }
        Ok(())
    }
    fn require_mask(&self, mask: &Arc<PreparedNativeMask>) -> Result<(), Error> {
        if self.masks.iter().any(|p| Arc::ptr_eq(p, mask)) {
            Ok(())
        } else {
            Err(Error::ResourceDomain)
        }
    }
}
fn same_frame(
    a: &crate::native_scene::execution::NativeCommandFrame,
    b: &crate::native_scene::execution::NativeCommandFrame,
) -> bool {
    fn state_bits(a: wire::State) -> [u8; 64] {
        let mut bytes = [0; 64];
        a.write_into(&mut bytes)
            .expect("complete original State wire extent");
        bytes
    }
    Arc::ptr_eq(a.scene(), b.scene())
        && a.command_index() == b.command_index()
        && a.target() == b.target()
        && a.presentation() == b.presentation()
        && a.dpi_scale().to_bits() == b.dpi_scale().to_bits()
        && a.source_state_dpi().map(f32::to_bits) == b.source_state_dpi().map(f32::to_bits)
        && a.ignores_alpha() == b.ignores_alpha()
        && state_bits(a.state()) == state_bits(b.state())
        && state_bits(a.source_state()) == state_bits(b.source_state())
}
