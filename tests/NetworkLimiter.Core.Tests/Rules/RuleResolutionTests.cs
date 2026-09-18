using NetworkLimiter.Core.Rules;

namespace NetworkLimiter.Core.Tests.Rules;

/// <summary>
/// Appariement règle ↔ application — FR-039, FR-039a, FR-039c.
/// </summary>
/// <remarks>
/// <para>
/// Le chemin complet prime ; le nom d'exécutable ne sert de repli que si le chemin enregistré
/// a disparu — cas typique d'une application mise à jour vers un dossier versionné.
/// </para>
/// <para>
/// Le piège est le repli trop large. Une règle visant <c>C:\Outils\updater.exe</c> ne doit pas
/// capturer un <c>updater.exe</c> sans rapport situé ailleurs tant que le chemin d'origine
/// existe. Et quand le repli s'applique, il doit être <b>signalé</b> : sans cela, l'utilisateur
/// ne comprendrait ni pourquoi une application qu'il n'a pas limitée rame, ni pourquoi celle
/// qu'il a limitée échappe parfois à sa règle.
/// </para>
/// </remarks>
public sealed class RuleResolutionTests
{
    private static AppIdentity App(string path, string? name = null, string display = "App") =>
        new(path, name ?? path[(path.LastIndexOf('\\') + 1)..], display);

    private static RuleTarget Rule(
        Guid id, string path, bool enabled = true, bool pathExists = true) =>
        new(id, App(path), enabled, pathExists);

    private static readonly Guid RuleA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RuleB = new("22222222-2222-2222-2222-222222222222");

    private static RuleResolver Resolver(params RuleTarget[] rules)
    {
        var resolver = new RuleResolver();
        resolver.Update(rules);
        return resolver;
    }

    // -- Appariement exact ----------------------------------------------------

    [Fact]
    public void CheminExact_EstAppariePar_ExactPath()
    {
        RuleResolver resolver = Resolver(Rule(RuleA, @"c:\program files\steam\steam.exe"));

        RuleMatch? match = resolver.Resolve(App(@"c:\program files\steam\steam.exe"));

        match.Should().NotBeNull();
        match!.RuleId.Should().Be(RuleA);
        match.Mode.Should().Be(RuleMatchMode.ExactPath);
    }

    [Fact]
    public void ProcessusSansRegle_NEstPasApparie()
    {
        // Son trafic n'est pas limite : dans le doute, on laisse passer.
        RuleResolver resolver = Resolver(Rule(RuleA, @"c:\a\a.exe"));

        resolver.Resolve(App(@"c:\b\b.exe")).Should().BeNull();
    }

    [Fact]
    public void RegleDesactivee_NApparieRien()
    {
        // FR-006 : elle reste definie mais inactive, et l'etat de sante en portera la raison.
        RuleResolver resolver = Resolver(Rule(RuleA, @"c:\a\a.exe", enabled: false));

        resolver.Resolve(App(@"c:\a\a.exe")).Should().BeNull();
        resolver.ActiveRuleCount.Should().Be(0);
    }

    // -- Repli sur le nom d'exécutable (FR-039a) ------------------------------

    [Fact]
    public void CheminDisparu_LeRepliSurLeNomSApplique()
    {
        // Le cas reel : l'application s'est mise a jour de app-1.2.3 vers app-1.2.4.
        RuleResolver resolver = Resolver(
            Rule(RuleA, @"c:\users\m\appdata\local\app\app-1.2.3\app.exe", pathExists: false));

        RuleMatch? match = resolver.Resolve(App(@"c:\users\m\appdata\local\app\app-1.2.4\app.exe"));

        match.Should().NotBeNull();
        match!.RuleId.Should().Be(RuleA);
        match.Mode.Should().Be(RuleMatchMode.FallbackName);
    }

    [Fact]
    public void CheminExistantToujours_LeRepliNeSAppliquePas()
    {
        // LE garde-fou contre un repli trop large. Tant que c:\outils\updater.exe existe, la
        // regle ne doit pas capturer un updater.exe sans rapport situe ailleurs.
        RuleResolver resolver = Resolver(
            Rule(RuleA, @"c:\outils\updater.exe", pathExists: true));

        resolver.Resolve(App(@"c:\autre\updater.exe")).Should().BeNull();
    }

    [Fact]
    public void LeCheminExact_PrimeSurLeRepli()
    {
        // Deux regles visent un app.exe : l'une par un chemin disparu, l'autre par le chemin
        // reel du processus. C'est la seconde qui doit gagner.
        RuleResolver resolver = Resolver(
            Rule(RuleA, @"c:\ancien\app.exe", pathExists: false),
            Rule(RuleB, @"c:\actuel\app.exe", pathExists: true));

        RuleMatch? match = resolver.Resolve(App(@"c:\actuel\app.exe"));

        match!.RuleId.Should().Be(RuleB);
        match.Mode.Should().Be(RuleMatchMode.ExactPath);
    }

    [Fact]
    public void RegleDesactiveeAuCheminDisparu_NAlimentePasLeRepli()
    {
        RuleResolver resolver = Resolver(
            Rule(RuleA, @"c:\ancien\app.exe", enabled: false, pathExists: false));

        resolver.Resolve(App(@"c:\nouveau\app.exe")).Should().BeNull();
    }

    [Fact]
    public void RepliAvecPlusieursCandidats_EstDeterministe()
    {
        // A candidats multiples, le meme processus doit toujours tomber sur la meme regle,
        // d'une execution a l'autre. Un choix instable produirait un plafond qui change
        // apparemment tout seul.
        RuleTarget[] rules =
        [
            Rule(RuleB, @"c:\b\app.exe", pathExists: false),
            Rule(RuleA, @"c:\a\app.exe", pathExists: false),
        ];

        RuleMatch? first = Resolver(rules).Resolve(App(@"c:\c\app.exe"));
        RuleMatch? second = Resolver([.. rules.Reverse()]).Resolve(App(@"c:\c\app.exe"));

        first!.RuleId.Should().Be(second!.RuleId);
        first.Mode.Should().Be(RuleMatchMode.FallbackName);
    }

    // -- Le mode d'appariement est toujours remonté (FR-039c) -----------------

    [Fact]
    public void LeModeDAppariement_EstToujoursRenseigne()
    {
        // FR-039c interdit l'appariement par repli silencieux : l'interface doit pouvoir le
        // signaler a cote de la regle concernee.
        RuleResolver exact = Resolver(Rule(RuleA, @"c:\a\app.exe"));
        RuleResolver fallback = Resolver(Rule(RuleA, @"c:\a\app.exe", pathExists: false));

        exact.Resolve(App(@"c:\a\app.exe"))!.Mode.Should().Be(RuleMatchMode.ExactPath);
        fallback.Resolve(App(@"c:\b\app.exe"))!.Mode.Should().Be(RuleMatchMode.FallbackName);
    }

    [Fact]
    public void ModeNone_NEstJamaisRenduAvecUneRegle()
    {
        // None signifie « aucune regle » ; le rendre avec un identifiant serait contradictoire.
        RuleResolver resolver = Resolver(Rule(RuleA, @"c:\a\app.exe"));

        resolver.Resolve(App(@"c:\a\app.exe"))!.Mode.Should().NotBe(RuleMatchMode.None);
        resolver.Resolve(App(@"c:\z\z.exe")).Should().BeNull();
    }

    // -- Mise à jour du jeu de règles -----------------------------------------

    [Fact]
    public void Update_RemplaceEntierementLeJeuDeRegles()
    {
        // Les regles retirees ne doivent laisser aucune trace : une regle supprimee qui
        // continuerait d'apparier limiterait une application que l'utilisateur croit libre.
        var resolver = new RuleResolver();
        resolver.Update([Rule(RuleA, @"c:\a\a.exe")]);

        resolver.Update([Rule(RuleB, @"c:\b\b.exe")]);

        resolver.Resolve(App(@"c:\a\a.exe")).Should().BeNull();
        resolver.Resolve(App(@"c:\b\b.exe"))!.RuleId.Should().Be(RuleB);
    }

    [Fact]
    public void JeuDeReglesVide_NApparieRien()
    {
        var resolver = new RuleResolver();
        resolver.Update([]);

        resolver.ActiveRuleCount.Should().Be(0);
        resolver.Resolve(App(@"c:\a\a.exe")).Should().BeNull();
    }

    [Fact]
    public void ActiveRuleCount_NeCompteQueLesReglesActives()
    {
        RuleResolver resolver = Resolver(
            Rule(RuleA, @"c:\a\a.exe", enabled: true),
            Rule(RuleB, @"c:\b\b.exe", enabled: false));

        resolver.ActiveRuleCount.Should().Be(1);
    }

    // -- Comparaison de chemins -----------------------------------------------

    [Fact]
    public void LaComparaison_EstOrdinaleSurDesCheminsDejaNormalises()
    {
        // La normalisation de casse appartient a PathNormalizer, en amont. Comparer ici de
        // facon insensible a la casse masquerait un defaut de normalisation au lieu de le
        // reveler.
        RuleResolver resolver = Resolver(Rule(RuleA, @"c:\a\app.exe"));

        resolver.Resolve(App(@"C:\A\APP.EXE", name: "app.exe")).Should().BeNull();
    }

    // -- Robustesse -----------------------------------------------------------

    [Fact]
    public void Update_SansRegles_Leve()
    {
        var resolver = new RuleResolver();

        Action act = () => resolver.Update(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_SansIdentite_Leve()
    {
        var resolver = new RuleResolver();

        Action act = () => resolver.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
