using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using NetworkLimiter.App.Elevation;
using NetworkLimiter.App.ViewModels;
using NetworkLimiter.App.Views;
using NetworkLimiter.Contracts.Messages;
using Xunit;

namespace NetworkLimiter.App.Tests;

/// <summary>
/// Charge réellement le XAML et éprouve les convertisseurs avec les types qu'ils recevront.
/// </summary>
/// <remarks>
/// <para>
/// Ces tests existent parce qu'une fenêtre WPF ne trahit rien à la compilation. Une ressource
/// manquante fait planter au chargement ; un convertisseur mal choisi ne signale <b>rien du
/// tout</b> et rend simplement un élément invisible pour toujours. C'est ce second cas qui est
/// arrivé : la superposition de l'éditeur était liée par le convertisseur de texte à un modèle
/// de vue, <c>value as string</c> rendait <c>null</c>, et l'éditeur ne se serait jamais ouvert.
/// Le bouton aurait paru inerte, sans une ligne d'erreur nulle part.
/// </para>
/// <para>
/// Aucun écran ni élévation requis : le XAML se charge sur un fil STA, et les
/// <c>StaticResource</c> sont résolues à ce moment-là.
/// </para>
/// </remarks>
public sealed class XamlLoadTests
{
    [Fact]
    public void LaFenetrePrincipale_SeChargeAvecToutesSesRessources()
    {
        RunOnStaThread(() =>
        {
            EnsureApplicationResources();

            // Un StaticResource introuvable leve ICI, a l'instanciation. C'est exactement la
            // « fenetre blanche au demarrage » que l'utilisateur constaterait autrement.
            var window = new MainWindow(BuildModel());

            window.Should().NotBeNull();
            window.DataContext.Should().BeOfType<MainViewModel>();
        });
    }

    [Fact]
    public void LEditeurDeRegle_SeChargeAvecSonGabaritDeSaisie()
    {
        RunOnStaThread(() =>
        {
            EnsureApplicationResources();

            var view = new RuleEditorView
            {
                DataContext = RuleEditorSessionViewModel.Create(SomeApp),
            };

            view.Should().NotBeNull();
        });
    }

    [Fact]
    public void LeGabaritDeSaisie_ExisteDansLesRessourcesDeLApplication()
    {
        RunOnStaThread(() =>
        {
            EnsureApplicationResources();

            // RuleEditorView le reference par StaticResource : son absence rendrait la vue
            // impossible a charger, donc l'edition impossible tout court.
            Application.Current!.Resources.Contains("RateInputTemplate").Should().BeTrue();
        });
    }

    [Theory]
    [InlineData("Not")]
    [InlineData("RateUnitIndex")]
    [InlineData("TextToVisibility")]
    [InlineData("NullToVisibility")]
    [InlineData("BoolToVisibility")]
    [InlineData("BoolToVisibilityInverted")]
    public void ChaqueConvertisseurReference_EstDeclare(string key)
    {
        RunOnStaThread(() =>
        {
            EnsureApplicationResources();

            Application.Current!.Resources.Contains(key).Should().BeTrue();
        });
    }

    [Fact]
    public void LaLiaisonDeLEditeur_RendVisibleUneSessionOuverte()
    {
        // LE test qui aurait attrapé le bug. Les autres éprouvent les convertisseurs isolément
        // et passaient donc tous : la faute n'était pas dans un convertisseur, mais dans le
        // CHOIX du convertisseur pour cette liaison-là. On interroge donc la liaison réelle,
        // telle que le XAML la déclare, avec le type qu'elle recevra en production.
        RunOnStaThread(() =>
        {
            EnsureApplicationResources();

            var window = new MainWindow(BuildModel());
            var overlay = (FrameworkElement)window.FindName("EditorOverlay");

            Binding binding = BindingOperations.GetBinding(overlay, UIElement.VisibilityProperty)!;

            binding.Should().NotBeNull("la superposition de l'éditeur doit être pilotée par une liaison");

            object result = binding.Converter.Convert(
                RuleEditorSessionViewModel.Create(SomeApp),
                typeof(Visibility),
                binding.ConverterParameter,
                CultureInfo.InvariantCulture);

            result.Should().Be(
                Visibility.Visible,
                "sinon l'éditeur ne s'ouvre jamais et le bouton paraît inerte, sans aucune erreur");

            binding.Converter.Convert(
                null, typeof(Visibility), binding.ConverterParameter, CultureInfo.InvariantCulture)
                .Should().Be(Visibility.Collapsed, "aucune session ouverte : rien à afficher");
        });
    }

    [Fact]
    public void LEditeur_EstVisibleDesQuUneSessionEstOuverte()
    {
        // LE bug, figé en test. Le convertisseur de texte rendait Collapsed pour tout objet
        // non textuel : l'éditeur restait invisible quoi qu'il arrive.
        var converter = new NullToVisibilityConverter();

        object visible = converter.Convert(
            RuleEditorSessionViewModel.Create(SomeApp), typeof(Visibility), null, CultureInfo.InvariantCulture);

        visible.Should().Be(Visibility.Visible);

        converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void LeConvertisseurDeTexte_NeSertQuAuTexte()
    {
        // Garde-fou explicite : rendre Collapsed pour un objet n'est pas un defaut de ce
        // convertisseur, c'est son contrat. Le defaut etait de l'employer hors de son domaine.
        var converter = new TextToVisibilityConverter();

        converter.Convert("un message", typeof(Visibility), null, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Visible);

        converter.Convert("   ", typeof(Visibility), null, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Collapsed);

        converter.Convert(new object(), typeof(Visibility), null, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void LeConvertisseurDUnite_FaitBienLAllerRetour()
    {
        var converter = new RateUnitIndexConverter();

        foreach (RateUnit unit in Enum.GetValues<RateUnit>())
        {
            object index = converter.Convert(unit, typeof(int), null, CultureInfo.InvariantCulture);

            converter.ConvertBack(index, typeof(RateUnit), null, CultureInfo.InvariantCulture)
                .Should().Be(unit, "l'unité choisie dans la liste doit revenir intacte");
        }

        // Un index hors bornes ne doit pas lever dans une liaison : WPF avalerait l'exception
        // et l'unite resterait silencieusement fausse.
        converter.ConvertBack(42, typeof(RateUnit), null, CultureInfo.InvariantCulture)
            .Should().Be(RateUnit.MegabytesPerSecond);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification =
            "La propriété du client passe au modèle de vue, comme en production. Le test ne " +
            "connecte jamais : aucun tuyau n'est ouvert, donc rien à libérer.")]
    private static MainViewModel BuildModel() =>
        new(new Ipc.PipeClient("NetworkLimiter.tests.absent"),
            new StubElevationLauncher(),
            new ImmediateDispatcher());

    private static ObservedAppDto SomeApp => new()
    {
        ExecutablePath = @"c:\jeux\jeu.exe",
        ExecutableName = "jeu.exe",
        AlreadyRuled = false,
    };

    /// <summary>
    /// Charge les ressources de l'application une seule fois pour tout le processus de test.
    /// </summary>
    /// <remarks>
    /// WPF n'admet qu'une instance d'<see cref="Application"/> par domaine ; en créer une
    /// seconde lève. Les tests xUnit d'une même classe partagent le processus, d'où ce garde.
    /// </remarks>
    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
        {
            return;
        }

        // La VRAIE classe App, pas une Application nue chargée d'un dictionnaire : c'est elle
        // qui s'exécutera en production, et c'est donc elle qu'il faut éprouver. Créer une
        // Application nue puis charger App.xaml échoue d'ailleurs, ce fichier déclarant lui
        // aussi une Application — WPF n'en admet qu'une par processus.
        var application = new global::NetworkLimiter.App.App();
        application.InitializeComponent();
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;

        // WPF exige un appartement STA. Un thread dedie evite d'imposer une contrainte
        // d'execution a toute la suite de tests.
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Le chargement XAML a échoué : {failure.Message}", failure);
        }
    }

    private sealed class StubElevationLauncher : IElevationLauncher
    {
        public bool IsElevated => false;

        public ElevationOutcome RequestElevation() =>
            ElevationOutcome.Declined;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
