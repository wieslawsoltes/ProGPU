use crate::{Error, vector::Matrix4};
#[derive(Default)]
pub(super) struct Clips(Vec<[f32; 4]>);
impl Clips {
    pub fn active(&self) -> Option<[f32; 4]> {
        self.0.last().copied()
    }
    pub fn pop(&mut self) {
        self.0.pop();
    }
    pub fn push(&mut self, rect: [f32; 4], transform: Matrix4) -> Result<(), Error> {
        if transform
            .0
            .iter()
            .flatten()
            .chain(&rect)
            .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        if transform.0[0][1].abs() > 0.0001 || transform.0[1][0].abs() > 0.0001 {
            return Err(Error::UnsupportedExecution);
        }
        let [x, y, w, h] = rect;
        let p =
            [[x, y], [x + w, y], [x + w, y + h], [x, y + h]].map(|p| transform.transform_point(p));
        let x1 = p[0][0].min(p[1][0]).min(p[2][0].min(p[3][0]));
        let y1 = p[0][1].min(p[1][1]).min(p[2][1].min(p[3][1]));
        let x2 = p[0][0].max(p[1][0]).max(p[2][0].max(p[3][0]));
        let y2 = p[0][1].max(p[1][1]).max(p[2][1].max(p[3][1]));
        let screen = [x1, y1, x2 - x1, y2 - y1];
        let clip = if let Some(old) = self.active() {
            let x1 = old[0].max(screen[0]);
            let y1 = old[1].max(screen[1]);
            let x2 = (old[0] + old[2]).min(screen[0] + screen[2]);
            let y2 = (old[1] + old[3]).min(screen[1] + screen[3]);
            [x1, y1, (x2 - x1).max(0.0), (y2 - y1).max(0.0)]
        } else {
            screen
        };
        if clip.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        self.0.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.0.push(clip);
        Ok(())
    }
}
