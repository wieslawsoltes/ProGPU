param([Parameter(Mandatory = $true)][string]$Directory)

$ErrorActionPreference = 'Stop'
# Preserve the original TRX and test exit code. Emit failures after the full test
# transcript so a bounded CI log read can still identify the original failure.
$results = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File -ErrorAction SilentlyContinue)
if ($results.Count -eq 0) {
    Write-Output 'No core TRX was produced; the preceding build/test result remains authoritative.'
    exit 0
}
foreach ($result in $results) {
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $reader = [System.Xml.XmlReader]::Create($result.FullName, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        foreach ($failure in $document.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='Failed']")) {
            [ordered]@{
                resultFile = $result.Name
                testName = $failure.GetAttribute('testName')
                outcome = $failure.GetAttribute('outcome')
                originalOutput = $failure.InnerText
            } | ConvertTo-Json -Compress -Depth 5 | Write-Output
        }
    } finally {
        $reader.Dispose()
    }
}
