using NetworkLimiter.App.ViewModels;

namespace NetworkLimiter.App.Tests;

/// <summary>
/// Saisie d'un plafond — FR-007.
/// </summary>
/// <remarks>
/// <para>
/// FR-007 impose de refuser une valeur hors bornes <b>à la saisie</b>, et d'indiquer les bornes
/// à ce moment-là. Le refus après validation fait perdre son travail à l'utilisateur et ne lui
/// dit pas ce qu'il aurait fallu taper.
/// </para>
/// <para>
/// L'unité est toujours visible, parce que c'est le malentendu le plus courant du domaine :
/// les offres commerciales annoncent des mégabits, l'outil manipule des méga-octets, et un
/// facteur huit sépare ce que l'utilisateur croit saisir de ce qu'il saisit.
/// </para>
/// </remarks>
public sealed class RateInputViewModelTests
{
    private const long Min = 10_240;              // 10 Ko/s
    private const long Max = 1_073_741_824;       // 1 Go/s

    private static RateInputViewModel Input(string text, RateUnit unit = RateUnit.MegabytesPerSecond) =>
        new() { Unlimited = false, Unit = unit, Text = text };

    // -- Illimité -------------------------------------------------------------

    [Fact]
    public void ParDefaut_LaSaisieEstIllimiteeEtValide()
    {
        var input = new RateInputViewModel();

        input.Unlimited.Should().BeTrue();
        input.IsValid.Should().BeTrue();
        input.BytesPerSecond.Should().BeNull();
    }

    [Fact]
    public void Illimite_IgnoreUneSaisieInvalide()
    {
        // L'utilisateur qui coche « illimite » apres avoir tape n'importe quoi ne doit pas
        // rester bloque par un message devenu sans objet.
        RateInputViewModel input = Input("n'importe quoi");
        input.IsValid.Should().BeFalse();

        input.Unlimited = true;

        input.IsValid.Should().BeTrue();
        input.BytesPerSecond.Should().BeNull();
    }

    // -- Conversion d'unité ---------------------------------------------------

    [Fact]
    public void UnMegaOctetParSeconde_VautLaValeurEnBase1024()
    {
        Input("1", RateUnit.MegabytesPerSecond).BytesPerSecond.Should().Be(1_048_576);
    }

    [Fact]
    public void DixKiloOctetsParSeconde_CorrespondentALaBorneBasse()
    {
        Input("10", RateUnit.KilobytesPerSecond).BytesPerSecond.Should().Be(Min);
    }

    [Fact]
    public void ValeurDecimale_EstAcceptee()
    {
        Input("1,5", RateUnit.MegabytesPerSecond).BytesPerSecond.Should().Be(1_572_864);
    }

    [Fact]
    public void LePointEtLaVirgule_SontTousDeuxAcceptes()
    {
        // L'utilisateur francais tape « 1,5 » au pave numerique mais « 1.5 » sur un clavier
        // programmeur. Refuser l'un des deux serait une friction gratuite.
        Input("1.5").BytesPerSecond.Should().Be(Input("1,5").BytesPerSecond);
    }

    [Fact]
    public void EspacesAutour_SontIgnores()
    {
        Input("  2  ").BytesPerSecond.Should().Be(2 * 1_048_576);
    }

    // -- Bornes ---------------------------------------------------------------

    [Fact]
    public void BorneBasseExacte_EstAcceptee()
    {
        Input("10", RateUnit.KilobytesPerSecond).IsValid.Should().BeTrue();
    }

    [Fact]
    public void JusteSousLaBorneBasse_EstRefuse()
    {
        RateInputViewModel input = Input("9", RateUnit.KilobytesPerSecond);

        input.IsValid.Should().BeFalse();
        input.BytesPerSecond.Should().BeNull();
    }

    [Fact]
    public void BorneHauteExacte_EstAcceptee()
    {
        Input("1024", RateUnit.MegabytesPerSecond).BytesPerSecond.Should().Be(Max);
    }

    [Fact]
    public void AuDessusDeLaBorneHaute_EstRefuse()
    {
        Input("2048", RateUnit.MegabytesPerSecond).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0,5")]
    public void ValeurNulleOuNegative_EstRefusee(string text)
    {
        // Zero serait un blocage total, hors perimetre (FR-028).
        Input(text).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("--5")]
    [InlineData("1e400")]
    public void SaisieNonNumerique_EstRefuseeSansLever(string text)
    {
        RateInputViewModel input = Input(text);

        input.IsValid.Should().BeFalse();
        input.ValidationMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SaisieVide_DemandeUneValeurOuLOptionIllimite()
    {
        RateInputViewModel input = Input("");

        input.IsValid.Should().BeFalse();
        input.ValidationMessage.Should().Contain("illimité");
    }

    // -- Les bornes sont annoncées (FR-007) -----------------------------------

    [Fact]
    public void LeMessageDeRefus_RappelleLesBornes()
    {
        // Sans cela, l'utilisateur sait que sa valeur est refusee mais pas ce qu'il devrait
        // taper a la place.
        RateInputViewModel input = Input("5000", RateUnit.MegabytesPerSecond);

        input.ValidationMessage.Should().NotBeNullOrWhiteSpace();
        input.ValidationMessage.Should().Contain("Mo/s");
    }

    [Fact]
    public void LesBornesAnnoncees_SuiventLUniteChoisie()
    {
        var input = new RateInputViewModel { Unit = RateUnit.KilobytesPerSecond };
        input.BoundsHint.Should().Contain("Ko/s");

        input.Unit = RateUnit.MegabytesPerSecond;
        input.BoundsHint.Should().Contain("Mo/s");
    }

    [Fact]
    public void ChangerDUnite_RevalideLaSaisie()
    {
        // « 500 » est valide en Ko/s mais hors bornes en Mo/s : l'interface doit le dire
        // immediatement apres le changement d'unite, pas au moment de valider.
        RateInputViewModel input = Input("2000", RateUnit.KilobytesPerSecond);
        input.IsValid.Should().BeTrue();

        input.Unit = RateUnit.MegabytesPerSecond;

        input.IsValid.Should().BeFalse();
    }

    // -- Chargement depuis une valeur existante -------------------------------

    [Fact]
    public void ChargerUnPlafondAbsent_DonneUneSaisieIllimitee()
    {
        var input = new RateInputViewModel();

        input.LoadFrom(null);

        input.Unlimited.Should().BeTrue();
        input.Text.Should().BeEmpty();
    }

    [Fact]
    public void ChargerUnPlafond_ChoisitLUniteLaPlusLisible()
    {
        // « 1,5 Mo/s » se lit mieux que « 1536 Ko/s ».
        var large = new RateInputViewModel();
        large.LoadFrom(1_572_864);

        var small = new RateInputViewModel();
        small.LoadFrom(Min);

        large.Unit.Should().Be(RateUnit.MegabytesPerSecond);
        large.Text.Should().Be("1,5");

        small.Unit.Should().Be(RateUnit.KilobytesPerSecond);
        small.Text.Should().Be("10");
    }

    [Fact]
    public void ChargerPuisRelire_PreserveLaValeur()
    {
        foreach (long value in (long[])[Min, 102_400, 1_048_576, 1_572_864, Max])
        {
            var input = new RateInputViewModel();
            input.LoadFrom(value);

            input.IsValid.Should().BeTrue($"la valeur {value} doit rester saisissable");
            input.BytesPerSecond.Should().Be(value);
        }
    }
}
