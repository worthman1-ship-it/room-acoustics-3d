using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>The ISO 3382-3 open-plan office single numbers: D2,S, the speech level at 4 m, and the comfort, distraction and privacy distances.</summary>
public class OpenPlanOfficeTests
{
    [Fact]
    public void TheStandardSpeechIs57Point4DbAAt1m()
    {
        // Table 1: normal unisex speech 1 m from an omnidirectional source in a free field, 57.4 dB(A); G is 20 dB there, so the speech is the table itself
        Assert.Equal(57.4, OpenPlanOffice.AWeightedDb(OpenPlanOffice.SpeechAt1mDb), 1);
        Assert.Equal(57.4, OpenPlanOffice.AWeightedDb(OpenPlanOffice.SpeechAt(OctaveSpectrum.Flat(20))), 1);
    }

    [Fact]
    public void InAFreeFieldSpeechFalls6DbPerDoubling()
    {
        // G = 20 - 20 lg r, so the speech falls 6.02 dB for each doubling of distance (2022, Note 1 to 3.2) and is 12.04 dB below its 1 m level at 4 m
        var at1m = OpenPlanOffice.AWeightedDb(OpenPlanOffice.SpeechAt1mDb);
        var positions = new[] { 2.0, 3, 5, 8, 12, 16, 20 }
            .Select(r => new OpenPlanOffice.Position(r, OpenPlanOffice.AWeightedDb(OpenPlanOffice.SpeechAt(OctaveSpectrum.Flat(20 - 20 * Math.Log10(r)))), 0.5)).ToList();
        var result = OpenPlanOffice.Evaluate(positions);
        Assert.Equal(20 * Math.Log10(2), result.SpatialDecayDb, 6);
        Assert.Equal(at1m - 20 * Math.Log10(4), result.SpeechAt4mDbA, 6);
        Assert.Equal(6, result.DecayPositions); // 20 m is past the 16 m the decay is taken over
        // rC: where the line reaches 45 dB(A)
        Assert.Equal(Math.Pow(10, (at1m - 45) / 20), result.ComfortDistanceM, 6);
    }

    [Fact]
    public void ComfortDistanceIsFormula8()
    {
        // 2022 Figure 3a: Lp,A,S,4m = 50 dB and D2,S = 6 dB give rC = 2^(17/6) = 7.1 m (the figure reads 7.2 m off the graph)
        var positions = new[] { 2.0, 4, 8, 16 }.Select(r => new OpenPlanOffice.Position(r, 50 - 6 * Math.Log2(r / 4), double.NaN)).ToList();
        var result = OpenPlanOffice.Evaluate(positions);
        Assert.Equal(6, result.SpatialDecayDb, 9);
        Assert.Equal(50, result.SpeechAt4mDbA, 9);
        Assert.Equal(Math.Pow(2, 17.0 / 6), result.ComfortDistanceM, 9);
    }

    [Fact]
    public void DistractionAndPrivacyDistancesComeFromAStraightLineThroughTheSti()
    {
        // STI = 0.80 - 0.03 r reaches 0.50 at 10 m and 0.20 at 20 m; the 1 m reference position is left out of the line (2022, 5.6)
        var positions = new[] { 2.0, 4, 6, 8, 12, 14 }.Select(r => new OpenPlanOffice.Position(r, 50, 0.8 - 0.03 * r))
            .Prepend(new OpenPlanOffice.Position(1, 57, 0.95)).ToList();
        var result = OpenPlanOffice.Evaluate(positions);
        Assert.Equal(10, result.DistractionDistanceM, 9);
        Assert.Equal(20, result.PrivacyDistanceM, 9);
        Assert.Equal(0.74, result.NearestSti, 9);
        Assert.Equal(6, result.StiPositions);
    }

    [Fact]
    public void NothingIsWorkedOutWithoutADecay()
    {
        // speech that does not fall with distance has no comfort distance, and an STI that does not fall has no distraction distance
        var positions = new[] { 2.0, 4, 8 }.Select(r => new OpenPlanOffice.Position(r, 50, 0.6)).ToList();
        var result = OpenPlanOffice.Evaluate(positions);
        Assert.Equal(0, result.SpatialDecayDb, 9);
        Assert.True(double.IsNaN(result.ComfortDistanceM));
        Assert.True(double.IsNaN(result.DistractionDistanceM));
        Assert.True(double.IsNaN(OpenPlanOffice.Evaluate(new[] { new OpenPlanOffice.Position(3, 50, 0.6) }).SpatialDecayDb));
    }
}
