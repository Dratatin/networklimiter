using NetworkLimiter.Core.Units;

namespace NetworkLimiter.Core.Tests.Units;

/// <summary>
/// Bornes de débit — FR-007 et data-model.md.
/// </summary>
/// <remarks>
/// Ces bornes sont un contrôle de sécurité autant qu'une commodité d'interface. Un plafond
/// non borné accepté par le service permettrait de saisir une valeur absurde (négative, ou
/// dépassant la capacité d'un <c>long</c> lors d'un calcul de jetons) et de mettre le shaper
/// dans un état indéfini. La validation doit donc vivre dans le type lui-même, pas dans
/// l'interface, pour qu'aucun chemin d'entrée ne puisse la contourner.
/// </remarks>
public sealed class ByteRateTests
{
    private const long Min = 10_240;              // 10 Ko/s
    private const long Max = 1_073_741_824;       // 1 Go/s

    [Fact]
    public void Constantes_CorrespondentAuContrat()
    {
        ByteRate.MinBytesPerSecond.Should().Be(Min);
        ByteRate.MaxBytesPerSecond.Should().Be(Max);
    }

    [Theory]
    [InlineData(Min)]
    [InlineData(Min + 1)]
    [InlineData(1_048_576)]
    [InlineData(Max - 1)]
    [InlineData(Max)]
    public void FromBytesPerSecond_DansLesBornes_EstAccepte(long value)
    {
        ByteRate rate = ByteRate.FromBytesPerSecond(value);

        rate.BytesPerSecond.Should().Be(value);
    }

    [Theory]
    [InlineData(Min - 1)]      // 10 239 : juste sous la borne basse
    [InlineData(Max + 1)]      // 1 073 741 825 : juste au-dessus de la borne haute
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void FromBytesPerSecond_HorsBornes_Leve(long value)
    {
        Action act = () => ByteRate.FromBytesPerSecond(value);

        act.Should().Throw<ArgumentOutOfRangeException>()
           .WithParameterName("bytesPerSecond");
    }

    [Theory]
    [InlineData(Min - 1, false)]
    [InlineData(Min, true)]
    [InlineData(Max, true)]
    [InlineData(Max + 1, false)]
    public void IsValid_RepondSansLever(long value, bool expected)
    {
        ByteRate.IsValid(value).Should().Be(expected);
    }

    [Fact]
    public void TryCreate_HorsBornes_RendFauxSansLever()
    {
        // L'interface valide a la saisie (FR-007) : elle a besoin d'un chemin qui ne leve pas.
        bool created = ByteRate.TryCreate(Min - 1, out ByteRate rate);

        created.Should().BeFalse();
        rate.Should().Be(default(ByteRate));
    }

    [Fact]
    public void TryCreate_DansLesBornes_RendVrai()
    {
        bool created = ByteRate.TryCreate(Max, out ByteRate rate);

        created.Should().BeTrue();
        rate.BytesPerSecond.Should().Be(Max);
    }

    [Fact]
    public void Egalite_EstStructurelle()
    {
        ByteRate a = ByteRate.FromBytesPerSecond(1_048_576);
        ByteRate b = ByteRate.FromBytesPerSecond(1_048_576);
        ByteRate c = ByteRate.FromBytesPerSecond(2_097_152);

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Comparaison_OrdonneParDebit()
    {
        // Necessaire pour FR-010 : le plafond effectif est le minimum entre regle et global.
        ByteRate small = ByteRate.FromBytesPerSecond(Min);
        ByteRate large = ByteRate.FromBytesPerSecond(Max);

        (small < large).Should().BeTrue();
        (large > small).Should().BeTrue();
        ByteRate.Min(small, large).Should().Be(small);
        ByteRate.Min(large, small).Should().Be(small);
    }

    [Theory]
    [InlineData(10_240, "10 Ko/s")]
    [InlineData(1_048_576, "1 Mo/s")]
    [InlineData(1_572_864, "1,5 Mo/s")]
    [InlineData(1_073_741_824, "1 Go/s")]
    public void ToString_AfficheUneUniteLisibleEtExplicite(long value, string expected)
    {
        // FR-007 : « l'unite toujours visible pour lever l'ambiguite avec les Kb/s
        // des offres commerciales ». Les unites sont en base 1024 (spec, Hypotheses).
        ByteRate.FromBytesPerSecond(value).ToString().Should().Be(expected);
    }
}
