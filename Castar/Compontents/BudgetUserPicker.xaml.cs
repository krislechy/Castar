using Castar.Domain.Models.DataBase;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class BudgetUserPicker : UserControl
{
    public static readonly DependencyProperty UsersProperty = DependencyProperty.Register(
        nameof(Users), typeof(IEnumerable<User>), typeof(BudgetUserPicker));

    public static readonly DependencyProperty SelectedUserProperty = DependencyProperty.Register(
        nameof(SelectedUser), typeof(User), typeof(BudgetUserPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IEnumerable<User>? Users
    {
        get => (IEnumerable<User>?)GetValue(UsersProperty);
        set => SetValue(UsersProperty, value);
    }

    public User? SelectedUser
    {
        get => (User?)GetValue(SelectedUserProperty);
        set => SetValue(SelectedUserProperty, value);
    }

    public BudgetUserPicker() => InitializeComponent();

    internal void ApplyListStyle(Style style) => UserList.Style = style;
}
