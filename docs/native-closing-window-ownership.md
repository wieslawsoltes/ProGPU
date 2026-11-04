# Ownership during a closing callback

A source window may open an owned confirmation dialog from its synchronous
`Closing` callback, before deciding whether to cancel the close. Silk's
`IsClosing` records close intent; it does not prove that the native owner has
been destroyed. `SilkWindowController.TrySetOwner` therefore admits that owner
while retaining the creating-thread, initialization, attached-platform, native
kind/display, self-ownership and cycle checks. The child must not be closing.

The controller compares the actual provider identity with the captured owner
handle before native binding and again before publishing the retained relation.
Disposal or replacement during a native callback rejects publication. Native
refusal remains false and native exceptions retain their original identity;
neither becomes a successful managed-only relationship. A rejected native setup
is not a rollback or lifetime lease: the source host still owns failed-window
teardown, and accepted source close must prevent later dialog creation.

`SilkWindowControllerOwnerTests` exercises the real controller, owned Cocoa
window and provider, with injected native operations. It covers closing callback
ownership, native refusal/exception, controller/window disposal, retirement
during binding, clearing, cycles and closing-child rejection. The fixture does
not create AppKit windows or qualify native desktop behavior.

Both managed and native-renderer source hosts use this shared controller; no
renderer, scene wire or C++ ownership implementation is duplicated here. The
matching LibreWPF host change admits an owner only during its synchronous close
callback and keeps accepted-close retirement separate. Full producer, source
application, desktop and package gates remain required before dependency pins
or release qualification.
