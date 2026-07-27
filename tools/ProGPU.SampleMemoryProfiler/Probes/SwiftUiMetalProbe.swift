import AppKit
import MetalKit
import SwiftUI

final class ProbeRenderer: NSObject, MTKViewDelegate {
    private let commandQueue: MTLCommandQueue
    private var frameCount: UInt64 = 0

    init?(view: MTKView) {
        guard
            let device = MTLCreateSystemDefaultDevice(),
            let commandQueue = device.makeCommandQueue()
        else {
            return nil
        }

        self.commandQueue = commandQueue
        super.init()

        view.device = device
        view.colorPixelFormat = .bgra8Unorm
        view.framebufferOnly = true
        view.enableSetNeedsDisplay = false
        view.isPaused = false
        view.preferredFramesPerSecond = 120
        view.clearColor = MTLClearColor(
            red: 0.035,
            green: 0.045,
            blue: 0.065,
            alpha: 1.0)
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
    }

    func draw(in view: MTKView) {
        guard
            let pass = view.currentRenderPassDescriptor,
            let drawable = view.currentDrawable,
            let commandBuffer = commandQueue.makeCommandBuffer(),
            let encoder = commandBuffer.makeRenderCommandEncoder(
                descriptor: pass)
        else {
            return
        }

        encoder.endEncoding()
        commandBuffer.present(drawable)
        commandBuffer.commit()
        frameCount &+= 1

        if frameCount == 1 {
            print(
                "[SwiftUiMetalProbe] device=\(view.device?.name ?? "unknown") " +
                "drawable=\(Int(view.drawableSize.width))x" +
                "\(Int(view.drawableSize.height))")
            fflush(stdout)
        }
    }
}

final class ProbeMetalHostView: NSView {
    private let metalView = MTKView(frame: .zero)
    private var renderer: ProbeRenderer?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)

        metalView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(metalView)
        NSLayoutConstraint.activate([
            metalView.leadingAnchor.constraint(equalTo: leadingAnchor),
            metalView.trailingAnchor.constraint(equalTo: trailingAnchor),
            metalView.topAnchor.constraint(equalTo: topAnchor),
            metalView.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])

        renderer = ProbeRenderer(view: metalView)
        metalView.delegate = renderer
    }

    required init?(coder: NSCoder) {
        nil
    }
}

struct ProbeMetalView: NSViewRepresentable {
    func makeNSView(context: Context) -> ProbeMetalHostView {
        ProbeMetalHostView(frame: .zero)
    }

    func updateNSView(_ nsView: ProbeMetalHostView, context: Context) {
    }
}

final class ProbeApplicationDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let content = NSHostingView(rootView: ProbeMetalView())
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 800, height: 600),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = "SwiftUI Metal Memory Probe"
        window.contentView = content
        window.center()
        window.makeKeyAndOrderFront(nil)
        self.window = window

        NSApplication.shared.activate(ignoringOtherApps: true)

        if
            let rawSeconds = ProcessInfo.processInfo.environment[
                "PROGPU_SWIFTUI_METAL_EXIT_AFTER_SECONDS"],
            let seconds = Double(rawSeconds),
            seconds > 0
        {
            DispatchQueue.main.asyncAfter(deadline: .now() + seconds) {
                NSApplication.shared.terminate(nil)
            }
        }
    }
}

let application = NSApplication.shared
let applicationDelegate = ProbeApplicationDelegate()
application.setActivationPolicy(.regular)
application.delegate = applicationDelegate
application.run()
