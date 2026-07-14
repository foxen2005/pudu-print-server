using System.Windows;
using PuduPrintServer.Services;

namespace PuduPrintServer.Views;

public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        BtnLogin.IsEnabled = false;
        BtnLogin.Content   = "Ingresando...";
        LblError.Visibility = Visibility.Collapsed;
        try
        {
            var auth = new AuthService();
            bool ok = await auth.LoginAsync(TxtEmail.Text.Trim(), TxtPassword.Password);
            if (ok) { DialogResult = true; Close(); }
            else    { LblError.Text = "Credenciales incorrectas."; LblError.Visibility = Visibility.Visible; }
        }
        catch (Exception ex)
        {
            LblError.Text = $"Error: {ex.Message}";
            LblError.Visibility = Visibility.Visible;
        }
        finally { BtnLogin.IsEnabled = true; BtnLogin.Content = "Ingresar"; }
    }
}
