// Alias globaux vers les types WPF.
//
// Ils dataient de l'époque où le projet référençait WinForms pour la zone de
// notification : ImplicitUsings importait alors System.Windows.Forms, dont
// plusieurs types portent le même nom que leurs équivalents WPF (KeyEventArgs,
// Point, Application…). WinForms a disparu du projet, mais ces alias restent
// utiles : ils rendent explicite le socle retenu et évitent d'avoir à qualifier
// les types au cas par cas.

global using Application = System.Windows.Application;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
global using MouseButton = System.Windows.Input.MouseButton;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using FontStyle = System.Windows.FontStyle;
global using Clipboard = System.Windows.Clipboard;
global using MessageBox = System.Windows.MessageBox;
global using TextBox = System.Windows.Controls.TextBox;
global using CheckBox = System.Windows.Controls.CheckBox;
global using ListBox = System.Windows.Controls.ListBox;
global using ComboBox = System.Windows.Controls.ComboBox;
global using Button = System.Windows.Controls.Button;
global using Label = System.Windows.Controls.Label;
global using Orientation = System.Windows.Controls.Orientation;
global using ContextMenu = System.Windows.Controls.ContextMenu;
global using MenuItem = System.Windows.Controls.MenuItem;
global using Image = System.Windows.Controls.Image;
global using Control = System.Windows.Controls.Control;
global using Separator = System.Windows.Controls.Separator;
global using PasswordBox = System.Windows.Controls.PasswordBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
global using FontFamily = System.Windows.Media.FontFamily;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using FlowDirection = System.Windows.FlowDirection;
global using Pen = System.Windows.Media.Pen;
