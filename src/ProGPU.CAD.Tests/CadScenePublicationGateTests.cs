using ACadSharp;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadScenePublicationGateTests
{
    [Fact]
    public void LatestMatchingGenerationPublishesExactlyOnce()
    {
        var session = new CadDocumentSession(new CadDocument());
        var gate = new CadScenePublicationGate();
        CadScenePublicationTicket ticket = gate.Begin(session);
        int publications = 0;

        Assert.True(gate.IsCurrent(ticket));
        Assert.True(gate.TryPublish(
            ticket,
            ticket.ContentGeneration,
            () => publications++));
        Assert.True(gate.IsCurrent(ticket));
        Assert.False(gate.TryPublish(
            ticket,
            ticket.ContentGeneration,
            () => publications++));
        Assert.Equal(1, publications);
    }

    [Fact]
    public void NewRequestAndInvalidationRejectOlderPreparedState()
    {
        var session = new CadDocumentSession(new CadDocument());
        var gate = new CadScenePublicationGate();
        CadScenePublicationTicket first = gate.Begin(session);
        CadScenePublicationTicket second = gate.Begin(session);
        bool published = false;

        Assert.False(gate.IsCurrent(first));
        Assert.False(gate.TryPublish(
            first,
            first.ContentGeneration,
            () => published = true));
        Assert.True(gate.IsCurrent(second));

        gate.Invalidate();

        Assert.False(gate.IsCurrent(second));
        Assert.False(gate.TryPublish(
            second,
            second.ContentGeneration,
            () => published = true));
        Assert.False(published);
    }

    [Fact]
    public void DocumentEditAndPreparedGenerationMismatchRejectPublication()
    {
        var session = new CadDocumentSession(new CadDocument());
        var gate = new CadScenePublicationGate();
        CadScenePublicationTicket mismatch = gate.Begin(session);
        bool published = false;

        Assert.False(gate.TryPublish(
            mismatch,
            mismatch.ContentGeneration + 1,
            () => published = true));

        CadScenePublicationTicket stale = gate.Begin(session);
        session.Edit("advance", static _ => { });

        Assert.False(gate.IsCurrent(stale));
        Assert.False(gate.TryPublish(
            stale,
            stale.ContentGeneration,
            () => published = true));
        Assert.False(published);
    }

    [Fact]
    public void TicketsCannotCrossPublicationGatesOrSessions()
    {
        var firstSession = new CadDocumentSession(new CadDocument());
        var secondSession = new CadDocumentSession(new CadDocument());
        var firstGate = new CadScenePublicationGate();
        var secondGate = new CadScenePublicationGate();
        CadScenePublicationTicket first = firstGate.Begin(firstSession);
        _ = firstGate.Begin(secondSession);

        Assert.False(secondGate.IsCurrent(first));
        Assert.False(secondGate.TryPublish(
            first,
            first.ContentGeneration,
            static () => throw new InvalidOperationException()));
        Assert.False(firstGate.IsCurrent(first));
    }
}
