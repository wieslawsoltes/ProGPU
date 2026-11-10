//! Original native ordered quadtree, with its independent bounds admission.
use super::*;
pub(super) fn build(
    primitives: &[wire::HitTestPrimitive],
) -> BuildResult<(Vec<wire::HitTestNode>, Vec<u32>)> {
    if primitives.len() > u32::MAX as usize
        || primitives.iter().any(|p| {
            !finite_point(p.bounds_min)
                || !finite_point(p.bounds_max)
                || p.bounds_min.x > p.bounds_max.x
                || p.bounds_min.y > p.bounds_max.y
        })
    {
        return Err(B::CapacityExceeded);
    }
    let mut tree = Tree {
        primitives,
        nodes: Vec::new(),
        indices: Vec::new(),
    };
    if primitives.is_empty() {
        push(&mut tree.nodes, wire::HitTestNode::default())?;
    } else {
        let leaf_estimate = ((primitives.len() + 31) / 32).max(1);
        let maximum_capacity = (primitives.len() * 2 + 1).min(65536);
        reserve_total(
            &mut tree.nodes,
            (1 + leaf_estimate * 2).clamp(1, maximum_capacity),
        )?;
        reserve_total(&mut tree.indices, primitives.len())?;
        let (mut min, mut max) = (primitives[0].bounds_min, primitives[0].bounds_max);
        for p in &primitives[1..] {
            min = p2(
                minimum(min.x, p.bounds_min.x),
                minimum(min.y, p.bounds_min.y),
            );
            max = p2(
                maximum(max.x, p.bounds_max.x),
                maximum(max.y, p.bounds_max.y),
            );
        }
        let mut root = Vec::new();
        reserve_total(&mut root, primitives.len())?;
        root.extend((0..primitives.len()).map(|i| i as u32));
        push(&mut tree.nodes, wire::HitTestNode::default())?;
        tree.fill(0, min, max, &root, 0)?;
    }
    Ok((tree.nodes, tree.indices))
}
struct Tree<'a> {
    primitives: &'a [wire::HitTestPrimitive],
    nodes: Vec<wire::HitTestNode>,
    indices: Vec<u32>,
}
impl Tree<'_> {
    fn leaf(
        &mut self,
        index: usize,
        min: wire::Point,
        max: wire::Point,
        values: &[u32],
    ) -> BuildResult<()> {
        let first = self.indices.len() as u32;
        reserve_append(&mut self.indices, values.len())?;
        self.indices.extend_from_slice(values);
        self.nodes[index] = wire::HitTestNode {
            bounds_min: min,
            bounds_max: max,
            first_primitive: first,
            primitive_count: values.len() as u32,
            ..Default::default()
        };
        Ok(())
    }
    fn fill(
        &mut self,
        index: usize,
        min: wire::Point,
        max: wire::Point,
        values: &[u32],
        depth: u32,
    ) -> BuildResult<()> {
        if depth >= 8 || values.len() <= 32 || (min.x == max.x && min.y == max.y) {
            return self.leaf(index, min, max, values);
        }
        let center = p2((min.x + max.x) * 0.5, (min.y + max.y) * 0.5);
        let mut retained = Vec::new();
        reserve_total(&mut retained, values.len())?;
        let mut children: [Vec<u32>; 4] = std::array::from_fn(|_| Vec::new());
        for &value in values {
            let p = self.primitives[value as usize];
            let left = p.bounds_max.x <= center.x;
            let right = p.bounds_min.x >= center.x;
            let top = p.bounds_max.y <= center.y;
            let bottom = p.bounds_min.y >= center.y;
            let child = if top && left {
                Some(0)
            } else if top && right {
                Some(1)
            } else if bottom && left {
                Some(2)
            } else if bottom && right {
                Some(3)
            } else {
                None
            };
            if let Some(child) = child {
                push(&mut children[child], value)?;
            } else {
                retained.push(value);
            }
        }
        let count = children.iter().filter(|v| !v.is_empty()).count();
        if count == 0 || (count == 1 && retained.is_empty()) {
            return self.leaf(index, min, max, values);
        }
        let first_primitive = self.indices.len() as u32;
        reserve_append(&mut self.indices, retained.len())?;
        self.indices.extend_from_slice(&retained);
        let first_child = u32::try_from(self.nodes.len()).map_err(|_| B::CapacityExceeded)?;
        let mut slots = [None; 4];
        for (i, c) in children.iter().enumerate() {
            if !c.is_empty() {
                slots[i] = Some(self.nodes.len());
                push(&mut self.nodes, wire::HitTestNode::default())?;
            }
        }
        self.nodes[index] = wire::HitTestNode {
            bounds_min: min,
            bounds_max: max,
            first_child,
            child_count: count as u32,
            first_primitive,
            primitive_count: retained.len() as u32,
        };
        let bounds = [
            (min, center),
            (p2(center.x, min.y), p2(max.x, center.y)),
            (p2(min.x, center.y), p2(center.x, max.y)),
            (center, max),
        ];
        for (i, c) in children.iter().enumerate() {
            if let Some(slot) = slots[i] {
                self.fill(slot, bounds[i].0, bounds[i].1, c, depth + 1)?;
            }
        }
        Ok(())
    }
}
