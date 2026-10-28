# Native owner registry callback lifetime

`NativeWindowOwnerRegistry` resolves a presentation handle to a weakly registered
typed owner. A provider's `IsAlive` or `NativeHandle` getter can synchronously
dispose its registration or register another owner under that presentation
handle. Lookup must not publish the old owner or delete the replacement after
such a callback.

Lookup now captures one exact registry entry, invokes provider getters outside
the registry lock, and checks the same entry after every callback. A dead owner
removes only that entry. Native-handle lookup rechecks entry identity immediately
after the native getter, then checks liveness and entry identity again before
publishing. It does not invoke the retired provider again or retry against a
replacement. Provider exceptions keep their original identity and propagation.

Registration replacement, token-based disposal and weak ownership remain
unchanged. A successful lookup is an observation at its final registry check,
not a native lifetime lease; consumers still own their subsequent native/source
operation checks and thread rules. No window/input action or platform admission
is added.

The existing owner-registry source controls cover replacement and retirement
during both getters, death during native-handle acquisition, a replacement from
the final liveness callback, no retry and unchanged provider exceptions. An
isolated build of the exact product sources reproduced seven failures against
the original implementation and passed all thirteen cases after the fix, with
zero skips. This source lifetime proof does not attribute a historical popup
application failure or complete the original application/package matrix in #197.
