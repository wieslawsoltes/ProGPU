export type Color = readonly [number, number, number, number];
export type Point = readonly [number, number];
/** Row-vector affine [m11,m12,m21,m22,m31,m32]. */
export type Transform = readonly [number, number, number, number, number, number];
export type Rect = readonly [number, number, number, number];
export interface LinearGradient {
    readonly type: 'linearGradient';
    readonly start: Point;
    readonly end: Point;
    readonly stops: readonly {readonly offset: number; readonly color: Color}[];
    readonly opacity?: number;
    readonly spread?: 'pad' | 'reflect' | 'repeat';
}
export type Brush = Color | LinearGradient;
export type StrokeCap = 'flat' | 'square' | 'round' | 'triangle';
export type StrokeJoin = 'miter' | 'bevel' | 'round';
export interface StrokeOptions {
    width?: number; closed?: boolean; startCap?: StrokeCap; endCap?: StrokeCap;
    lineJoin?: StrokeJoin; dashCap?: StrokeCap; miterLimit?: number;
    /** Alternating on/off thickness multipliers; odd counts repeat. */
    dashes?: readonly number[];
    dashOffset?: number; transform?: Transform;
}
export class Path {
    constructor();
    moveTo(x: number, y: number): this;
    lineTo(x: number, y: number): this;
    quadraticTo(cx: number, cy: number, x: number, y: number): this;
    cubicTo(c1x: number, c1y: number, c2x: number, c2y: number, x: number, y: number): this;
    close(): this;
}
export class Scene {
    private constructor();
    readonly sceneId: bigint;
    readonly generation: bigint;
}
export class SceneBuilder {
    constructor(options?: {sceneId?: bigint; generation?: bigint});
    fillRect(x: number, y: number, width: number, height: number, brush: Brush, options?: {transform?: Transform}): this;
    fillPath(path: Path, brush: Brush, options?: {fillRule?: 'nonzero' | 'evenodd'; transform?: Transform}): this;
    strokePolyline(points: readonly Point[], brush: Brush, options?: StrokeOptions): this;
    /** Absolute transform/opacity. clipRect is in logical target coordinates. */
    save(options?: {transform?: Transform; opacity?: number; clipRect?: Rect}): this;
    restore(): this;
    /** Isolated source-over compositing; opacity is applied once on pop. */
    pushLayer(options?: {opacity?: number; bounds?: Rect}): this;
    popLayer(): this;
    /** Snapshots authoring data; unbalanced scopes are rejected. */
    build(): Scene;
}
export interface SceneUpdateMetrics {
    readonly sceneId: bigint; readonly generation: bigint;
    readonly commandCount: number; readonly resourceCount: number;
    readonly streamBytes: number; readonly snapshotReused: boolean;
    readonly drawCount: number; readonly payloadBytes: number;
}
export interface FrameMetrics {
    readonly commandCount: number; readonly drawCallCount: number; readonly familySwitchCount: number;
    readonly submissionCount: bigint; readonly payloadHash: bigint;
    readonly vertexUploadBytes: number; readonly indexUploadBytes: number;
    readonly textureUploadBytes: number; readonly uniformUploadBytes: number;
    readonly coverageStagingBytes: number; readonly brushUploadBytes: number;
    readonly gradientStopUploadBytes: number; readonly textStyleUploadBytes: number;
    readonly colorGlyphUploadBytes: number;
}
export interface CanvasMetrics {
    readonly width: number; readonly height: number;
    readonly logicalWidth: number; readonly logicalHeight: number; readonly scale: number;
}
export interface Renderer {
    readonly device: GPUDevice;
    readonly error: Error | null;
    /** Logical extent with explicit physical-pixel scale (1..4). */
    resize(options: {width: number; height: number; pixelRatio?: number}): CanvasMetrics;
    /** One changed immutable generation, or a complete native stream. */
    updateScene(scene: Scene | Uint8Array): SceneUpdateMetrics;
    /** Independent copy of the last accepted full native stream. */
    getSceneStream(): Uint8Array;
    /** Submits GPU work; return is not a GPU/display completion fence. */
    render(options?: {clearColor?: Color}): FrameMetrics;
    /** Idempotent. Never destroys a caller-supplied GPUDevice. */
    dispose(): void;
}
/** Browser DOM + WebGPU only; no Canvas2D/WebGL/software renderer fallback.
 * The caller owns requestAnimationFrame, CSS sizing and resize notifications.
 * TypeScript 6+ DOM declarations provide GPUDevice. TypeScript 5 consumers must
 * explicitly enable supplemental @webgpu/types; do not mix those declarations
 * with newer DOM libraries that already define WebGPU. */
export function createRenderer(options: {
    canvas: HTMLCanvasElement; device?: GPUDevice; onError?: (error: Error) => void;
}): Promise<Renderer>;
