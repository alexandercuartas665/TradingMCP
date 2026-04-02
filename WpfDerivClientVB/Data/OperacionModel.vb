Imports System

Namespace WpfDerivClientVB
    Public Class OperacionModel
        Public Property Id As Integer
        Public Property ClientId As Integer
        Public Property ContractId As String
        Public Property Tipo As String        ' CALL / PUT
        Public Property Simbolo As String
        Public Property Monto As Decimal
        Public Property Duracion As String    ' "5 ticks"
        Public Property Estado As String      ' Abierto / Ganado / Perdido
        Public Property ProfitLoss As Decimal
        Public Property Hora As String
        Public Property CreatedAt As DateTime = DateTime.UtcNow
    End Class
End Namespace
