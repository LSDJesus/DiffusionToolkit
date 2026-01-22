using System;
using System.Windows;
using Dapper;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Pages
{
    public partial class DatabaseProfileDialog : Window
    {
        public DatabaseProfile? Profile { get; private set; }

        public DatabaseProfileDialog(DatabaseProfile? existingProfile, Window owner)
        {
            InitializeComponent();
            Owner = owner;

            if (existingProfile != null)
            {
                // Edit mode
                ProfileNameTextBox.Text = existingProfile.Name;
                DescriptionTextBox.Text = existingProfile.Description ?? "";
                ConnectionStringTextBox.Text = existingProfile.ConnectionString;
                SchemaTextBox.Text = existingProfile.Schema;
                ColorTextBox.Text = existingProfile.Color ?? "#4CAF50";
                ReadOnlyCheckBox.IsChecked = existingProfile.IsReadOnly;
                Title = $"Edit Profile: {existingProfile.Name}";
            }
            else
            {
                // Add mode
                Title = "New Database Profile";
                SchemaTextBox.Text = "public";
                ColorTextBox.Text = "#4CAF50";
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            var connectionString = ConnectionStringTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                MessageBox.Show(this, "Please enter a connection string first.", "Test Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TestConnectionButton.IsEnabled = false;
            TestConnectionButton.Content = "Testing...";

            try
            {
                var dataStore = new PostgreSQLDataStore(connectionString);
                dataStore.CurrentSchema = SchemaTextBox.Text.Trim();
                
                // Test connection by trying to query schema version
                using var conn = dataStore.OpenConnection();
                var result = conn.QueryFirstOrDefault<int?>("SELECT MAX(version) FROM schema_version");
                
                MessageBox.Show(this, 
                    $"✓ Connection successful!\n\nSchema version: {result ?? 0}", 
                    "Test Connection", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, 
                    $"✗ Connection failed:\n\n{ex.Message}", 
                    "Test Connection", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
            finally
            {
                TestConnectionButton.IsEnabled = true;
                TestConnectionButton.Content = "Test Connection";
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var name = ProfileNameTextBox.Text.Trim();
            var connectionString = ConnectionStringTextBox.Text.Trim();
            var schema = SchemaTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "Profile name is required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                MessageBox.Show(this, "Connection string is required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(schema))
            {
                schema = "public";
            }

            Profile = new DatabaseProfile
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim(),
                ConnectionString = connectionString,
                Schema = schema,
                Color = ColorTextBox.Text.Trim(),
                IsReadOnly = ReadOnlyCheckBox.IsChecked ?? false
            };

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
