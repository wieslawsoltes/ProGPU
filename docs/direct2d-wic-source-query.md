# WIC source query failure ownership

Portable `CreateBitmapFromWicBitmap` must not return successful creation with a
null bitmap when a foreign source reports a successful `QueryInterface` without
an interface. This malformed source response now returns `E_FAIL`. Genuine
failed HRESULTs remain unchanged, and the existing local COM owner releases an
interface returned alongside a failure exactly once. No metadata or pixel method
runs after either rejection, and no target commands or resources are changed.

This is a defensive adapter contract, not a claim about original Direct2D's
response to a broken COM object. Microsoft's
[QueryInterface contract](https://learn.microsoft.com/en-us/windows/win32/api/unknwn/nf-unknwn-iunknown-queryinterface%28refiid_void%29)
requires a retained requested interface on success, while
[CreateBitmapFromWicBitmap](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-createbitmapfromwicbitmap%28iwicbitmapsource_constd2d1_bitmap_properties_id2d1bitmap%29)
publishes the created bitmap on success. The normal source interface, copied
format/alpha/DPI policy and pixel ownership are unchanged.

Six authored controls cover null success (`S_OK` and `S_FALSE`), null and owned
interface returns with `E_NOINTERFACE` and `E_OUTOFMEMORY`, exact retained error,
zero metadata/pixel callbacks, paired AddRef/Release and complete target-stream
byte preservation. Existing successful imports and subsequent bitmap drawing
remain in the same fixture. These controls have not executed.

The change is shared by both native render providers through the portable COM
adapter. The separate original-Windows command-list reader does not implement
this bitmap-creation method; managed Scene has no WIC COM ingress. No renderer,
shader, public API, wire, ABI version, pipeline, GPU operation or managed/native
crossing changes. The extra failure selection is O(1), allocation-free and
outside successful image-copy work. Implementation is original ProGPU code;
no foreign implementation was used. All validation remains deferred.
