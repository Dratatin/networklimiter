// Windows Forms n'est référencé que pour NotifyIcon (zone de notification, T121), et il apporte
// une dizaine de types homonymes de ceux de WPF : Application, UserControl, MessageBox, Binding…
//
// Ces alias tranchent une fois pour toutes, au niveau du projet : dans cette interface, un nom
// non qualifié désigne le type WPF. L'alternative — qualifier au cas par cas — laisserait la
// prochaine classe créée choisir par accident le mauvais type, avec un message d'erreur qui ne
// dirait rien de la cause.
//
// Le code de la zone de notification, lui, nomme explicitement ses types Windows Forms.

global using Application = System.Windows.Application;
global using Binding = System.Windows.Data.Binding;
global using Clipboard = System.Windows.Clipboard;
global using MessageBox = System.Windows.MessageBox;
global using UserControl = System.Windows.Controls.UserControl;
