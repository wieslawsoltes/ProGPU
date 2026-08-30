using ProGPU.CAD;

var session = CadDocumentSession.CreateNew();
var version = session.Read(static document => document.Header.Version);
Console.WriteLine($"ProGPU.CAD package consumer created {version}.");
