# Native close-button enabled state

`SilkWindowController.SetCloseButtonEnabled` selects the enabled state of the
existing native Close control independently of `SetCanClose`, which retains its
original visibility semantics. The optional state is unselected by default, so
ordinary Win32 chrome refresh does not enable a system-menu command disabled by
another caller. Reapplication retains an explicitly selected value.

Windows updates the actual `SC_CLOSE` item in the owned window's system menu and
redraws its menu bar without removing `WS_SYSMENU`. macOS updates the actual Close
button's enabled state while preserving its visibility and style-mask policy.
X11 changes only the Motif Close function; decoration bits remain independent.
X11 publishes an advisory hint, not proof that a window manager honors it.

The operation neither disables window input nor intercepts programmatic close.
Source frameworks retain their own close/response policy. Unsupported native
providers return false. A false result does not establish native application.

The Windows hidden-window control checks the real system-menu state, preserved
minimize/maximize commands, input-enabled state, reapplication and independent
visibility. The existing X11 server-state lane checks actual Motif property values without
a window manager; this does not establish desktop enforcement. Existing
owner/modal-state lifetime controls remain independent. Platform-skipped
tests are not native qualification; original desktop and package acceptance
remain separate required gates.
