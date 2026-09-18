using System.Reflection;
using NetworkLimiter.Integration.Tests.Stories;

namespace NetworkLimiter.Integration.Tests.Guards;

/// <summary>
/// Empêche les tests d'assemblage de sortir de la CI sans que personne ne le voie.
/// </summary>
/// <remarks>
/// <para>
/// La CI exécute ce projet avec <c>--filter Requires!=Elevation</c>. Marquer
/// <see cref="ApplyRuleTests"/> comme exigeant l'élévation — par recopie d'un attribut, ou pour
/// faire taire un échec local — le retirerait de la CI <b>sans aucun signal</b> : la suite
/// resterait verte en vérifiant moins.
/// </para>
/// <para>
/// Ce projet a déjà connu cette panne exacte, ailleurs : un garde-fou de déterminisme est resté
/// vert vingt-deux commits durant parce que son filtre ne correspondait à aucun test, et que
/// « zéro test trouvé » n'est pas une erreur pour <c>dotnet test</c>.
/// </para>
/// </remarks>
public sealed class CiCoverageGuardTests
{
    [Fact]
    public void LesTestsDAssemblage_RestentExecutablesSansElevation()
    {
        IEnumerable<Attribute> traits = typeof(ApplyRuleTests)
            .GetCustomAttributes(inherit: true)
            .OfType<Attribute>()
            .Where(attribute => attribute.GetType().Name.Contains("Trait", StringComparison.Ordinal));

        traits.Should().BeEmpty(
            "ces tests montent le pipeline avec des intercepteurs simulés : ils n'exigent ni " +
            "pilote ni élévation, et les marquer les retirerait silencieusement de la CI");
    }

    [Fact]
    public void LeGardeFou_VoitBienDesTestsDAssemblage()
    {
        // Sans ce controle, renommer ou deplacer ApplyRuleTests ferait passer le test
        // precedent en ne verifiant rien — exactement le defaut qu'il existe pour empecher.
        MethodInfo[] facts = typeof(ApplyRuleTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes()
                .Any(attribute => attribute.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .ToArray();

        facts.Should().HaveCountGreaterThanOrEqualTo(
            5, "US1 exige de couvrir l'ajout, la modification, le retrait et la non-affectation des autres");
    }
}
