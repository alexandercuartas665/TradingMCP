Imports System
Imports System.ComponentModel

Namespace WpfDerivClientVB
    Public Class OperacionModel
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub Notify(name As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

        Public Property Id As Integer
        Public Property ClientId As Integer
        Public Property ContractId As String
        Public Property Hora As String
        Public Property CreatedAt As DateTime = DateTime.UtcNow
        Public Property Monto As Decimal
        Public Property Duracion As String

        Private _tipo As String = "?"
        Public Property Tipo As String
            Get
                Return _tipo
            End Get
            Set(v As String)
                _tipo = v
                Notify("Tipo")
            End Set
        End Property

        Private _simbolo As String = ""
        Public Property Simbolo As String
            Get
                Return _simbolo
            End Get
            Set(v As String)
                _simbolo = v
                Notify("Simbolo")
            End Set
        End Property

        Private _estado As String = "Abierto"
        Public Property Estado As String
            Get
                Return _estado
            End Get
            Set(v As String)
                _estado = v
                Notify("Estado")
                Notify("EstadoColor")
                Notify("EsAbierto")
            End Set
        End Property

        Public ReadOnly Property EsAbierto As Boolean
            Get
                Return _estado = "Abierto"
            End Get
        End Property

        Private _profitLoss As Decimal = 0
        Public Property ProfitLoss As Decimal
            Get
                Return _profitLoss
            End Get
            Set(v As Decimal)
                _profitLoss = v
                Notify("ProfitLoss")
                Notify("PLColor")
            End Set
        End Property

        Private _entrySpot As String = "-"
        Public Property EntrySpot As String
            Get
                Return _entrySpot
            End Get
            Set(v As String)
                _entrySpot = v
                Notify("EntrySpot")
            End Set
        End Property

        Private _spotActual As String = "-"
        Public Property SpotActual As String
            Get
                Return _spotActual
            End Get
            Set(v As String)
                _spotActual = v
                Notify("SpotActual")
            End Set
        End Property

        Private _tiempoRestante As String = "-"
        Public Property TiempoRestante As String
            Get
                Return _tiempoRestante
            End Get
            Set(v As String)
                _tiempoRestante = v
                Notify("TiempoRestante")
            End Set
        End Property

        Public ReadOnly Property PLColor As String
            Get
                If _profitLoss > 0 Then Return "#A6E3A1"
                If _profitLoss < 0 Then Return "#F38BA8"
                Return "#CDD6F4"
            End Get
        End Property

        Public ReadOnly Property EstadoColor As String
            Get
                Select Case _estado
                    Case "Ganado"
                        Return "#A6E3A1"
                    Case "Perdido"
                        Return "#F38BA8"
                    Case Else
                        Return "#89B4FA"
                End Select
            End Get
        End Property
    End Class
End Namespace
