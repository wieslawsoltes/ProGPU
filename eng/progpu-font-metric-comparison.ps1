# Only the four computed floating metrics have this bound. Identity, size,
# integer Height, family metrics, DPI, roles and units retain exact comparisons.
# GDI+ x64 differs by two single-precision ULPs even between its own implicit
# and explicit-DPI APIs. Portable arithmetic remains independently exact.
function Assert-NativeFontMetric([single] $Expected, [single] $Actual, [string] $Name) {
    if (-not [single]::IsFinite($Expected) -or -not [single]::IsFinite($Actual) -or
        $Expected -le 0 -or $Actual -le 0) { throw "$Name requires finite positive font metrics." }
    $distance = [Math]::Abs([long][BitConverter]::SingleToInt32Bits($Expected) -
                          [long][BitConverter]::SingleToInt32Bits($Actual))
    if ($distance -gt 2) { throw "${Name}: expected '$Expected', actual '$Actual', distance $distance ULPs (maximum 2)." }
}

function Assert-PortableFontArithmetic($Reference, $Actual, [string] $Name) {
    foreach ($property in @('SizeInPoints', 'ImplicitHeight', 'Explicit96', 'Explicit192')) {
        if ($Actual.$property -cne $Reference.PortableArithmetic.$property -or
            $Actual.PortableArithmetic.$property -cne $Reference.PortableArithmetic.$property) {
            throw "$Name/$property does not match exact portable unit arithmetic."
        }
    }
}
