Imports System.Windows
Imports System.Windows.Controls

Partial Public Class AdminSidebar
    Inherits UserControl

    ' Evento para notificar al Dashboard que un ítem del menú fue seleccionado
    Public Event MenuItemSelected(menuName As String)

    Public Sub New()
        InitializeComponent()
    End Sub

    Private Sub NavBtn_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return

        Dim menuName As String = btn.Name.Replace("btnNav", "")
        RaiseEvent MenuItemSelected(menuName)
    End Sub
End Class
