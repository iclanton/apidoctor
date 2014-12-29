﻿namespace OneDrive.ApiDocumentation.Validation
{
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System.Diagnostics;
    using System.ComponentModel;

	public class DocumentPublisher
	{
        public event EventHandler<ValidationError> NewMessage;

        /// <summary>
        /// Comma separated list of file extensions that should be scanned for internal content
        /// </summary>
        public string TextFileExtensions { get; set; }

        /// <summary>
        /// Full path to the source folder for documentation
        /// </summary>
        public string RootPath { get; private set; }

        /// <summary>
        /// Semicolon separated values of pathes that should be excluded from publishing.
        /// </summary>
        public string SkipPaths { get; set; }

        /// <summary>
        /// Indicates if non-text files are also published to the output directory
        /// </summary>
        public bool PublishAllFiles { get; set; }

        /// <summary>
        /// Output log
        /// </summary>
        public BindingList<ValidationError> Messages { get; private set; }

        /// <summary>
        /// Include verbose log messages in the output log
        /// </summary>
        public bool VerboseLogging { get; set; }


        private List<string> scannableExtensions;
        private List<string> ignoredPaths;

		public DocumentPublisher(string sourceFolder)
		{
            RootPath = new DirectoryInfo(sourceFolder).FullName;
            if (!RootPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                RootPath = string.Concat(RootPath, Path.DirectorySeparatorChar);

            TextFileExtensions = ".md,.mdown";
            SkipPaths = "\\internal;\\.git;\\legacy;\\generate_html_docs;\\.gitignore";
            Messages = new BindingList<ValidationError>();
		}

        private void LogMessage(ValidationError message)
        {
            var eventHandler = NewMessage;
            if (null != eventHandler)
            {
                eventHandler(this, message);
            }
            Messages.Add(message);
        }

        /// <summary>
        /// Sanitizes document formats and outputs them to the outputFolder.
        /// </summary>
        /// <param name="outputFolder"></param>
        /// <returns></returns>
		public async Task PublishToFolderAsync(string outputFolder)
		{
            Messages.Clear();

            DirectoryInfo destination = new DirectoryInfo(outputFolder);
            scannableExtensions = new List<string>(TextFileExtensions.Split(','));
            ignoredPaths = new List<string>(SkipPaths.Split(';'));

			await CleanDirectory(new DirectoryInfo(RootPath), destination);
		}

		/// <summary>
		/// Returns the relative directory for the passed directory based on the
		/// RootPath property.
		/// </summary>
		/// <returns>The directory path.</returns>
		/// <param name="dir">Dir.</param>
		private string RelativeDirectoryPath(DirectoryInfo dir, bool includeRootSpecifier)
		{
			var fullPath = dir.FullName;
			if (!fullPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
			{
				fullPath = string.Concat(fullPath, Path.DirectorySeparatorChar);
			}

			if (fullPath.Equals(RootPath))
			{
				return includeRootSpecifier ? Path.DirectorySeparatorChar.ToString() : string.Empty;
			}
			else if (fullPath.StartsWith(RootPath))
			{
				if (includeRootSpecifier)
					return Path.DirectorySeparatorChar + fullPath.Substring(RootPath.Length);
				else
					return fullPath.Substring(RootPath.Length);
			}

			Debug.Assert(false, "Failed to find a relative path for {0} from {1}", dir.FullName, RootPath);
			return null;
		}

		/// <summary>
		/// Recursively Scan the contents of a directory and remove any
		/// internal comments in the markdown documents
		/// </summary>
		/// <param name="directory">Directory.</param>
		private async Task CleanDirectory(DirectoryInfo directory, DirectoryInfo destinationRoot)
		{
			var pathDisplayName = RelativeDirectoryPath(directory, true);

			var filesInDirectory = directory.GetFiles();
			if (filesInDirectory.Length > 0)
			{
				// Create folder in the destination

				var relativePath = RelativeDirectoryPath(directory, false);
                var newDirectoryPath = Path.Combine(destinationRoot.FullName, relativePath);
				var newDirectory = new DirectoryInfo(newDirectoryPath);
				if (!newDirectory.Exists)
				{
                    LogMessage(new ValidationMessage(pathDisplayName, "Creating new directory in output folder."));
					newDirectory.Create();
				}
			}

			foreach (var file in filesInDirectory)
			{
				if (IsInternalPath(file))
				{
                    LogMessage(new ValidationMessage(file.Name, "Source file was on the internal path list, skipped."));
				}
				else if (IsScannableFile(file))
				{
					await WriteCleanFileToOutputAsync(file, destinationRoot);
				}
				else if (CopyToOutput(file))
				{
					CopyFileToOutput(file, destinationRoot);
				}
				else
				{
                    LogMessage(new ValidationWarning(file.Name, "Source file was not in the scan or copy list, skipped."));
				}
			}

			var subfolders = directory.GetDirectories();
			foreach (var folder in subfolders)
			{
				if (IsInternalPath(folder))
				{
                    LogMessage(new ValidationMessage(folder.Name, "Source file was on the internal path list, skipped."));
					// Skip output for that directory
					continue;
				}
				else
				{
					var displayName = RelativeDirectoryPath(folder, true);
                    LogMessage(new ValidationMessage(folder.Name, "Scanning directory."));
					await CleanDirectory(folder, destinationRoot);
				}
			}
		}

		private string OutputPathForInputFile(FileInfo file, DirectoryInfo destinationRoot)
		{
			var relativePath = RelativeDirectoryPath(file.Directory, false);
			var outputPath = Path.Combine(destinationRoot.FullName, relativePath, file.Name);
			return outputPath;
		}

		private void CopyFileToOutput(FileInfo file, DirectoryInfo destinationRoot)
		{
			try
			{
				var outPath = OutputPathForInputFile(file, destinationRoot);
                LogMessage(new ValidationMessage(file.Name, "Copying to output directory without scanning."));
                file.CopyTo(outPath, true);
			}
			catch (Exception ex)
			{
                LogMessage(new ValidationError(file.Name, "Cannot copy file to output directory: {0}", ex.Message));
			}
		}

		/// <summary>
		/// Scans the text content of a file and removes any "internal" comments/references
		/// </summary>
		/// <param name="file">File.</param>
		private async Task WriteCleanFileToOutputAsync(FileInfo file, DirectoryInfo destinationRoot)
		{
            LogMessage(new ValidationMessage(file.Name, "Scanning text file for internal content."));

			var outputPath = OutputPathForInputFile(file, destinationRoot);
			StreamWriter writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8);
			writer.AutoFlush = true;

			StreamReader reader = new StreamReader(file.OpenRead());

			long lineNumber = 0;
			string nextLine;
			while ( (nextLine = await reader.ReadLineAsync()) != null)
			{
				lineNumber++;
				if (IsDoubleBlockQuote(nextLine))
				{
                    LogMessage(new ValidationMessage(string.Concat(file, ":", lineNumber), "Removing DoubleBlockQuote: {0}", nextLine));
					continue;
				}
				await writer.WriteLineAsync(nextLine);
			}
			writer.Close();
			reader.Close();
		}

		#region Scanning Rules

		[ScanRuleAttribute(ScanRuleTarget.LineOfText)]
		private bool IsDoubleBlockQuote(string text)
		{
			return text.StartsWith(">>") || text.StartsWith(" >>");
		}

		[ScanRuleAttribute(ScanRuleTarget.FileInfo)]
		public bool IsScannableFile(FileInfo file)
		{
			return scannableExtensions.Contains(file.Extension);
		}

		public bool CopyToOutput(FileInfo file)
		{
			return PublishAllFiles;
		}

		[ScanRuleAttribute(ScanRuleTarget.FileInfo)]
		public bool IsInternalPath(DirectoryInfo folder)
		{
			var relativePath = RelativeDirectoryPath(folder, true);
			return IsRelativePathInternal(relativePath);
		}

		[ScanRuleAttribute(ScanRuleTarget.FileInfo)]
		public bool IsInternalPath(FileInfo file)
		{
			var relativePath = Path.Combine(RelativeDirectoryPath(file.Directory, true), file.Name);
			return IsRelativePathInternal(relativePath);
		}

		private bool IsRelativePathInternal(string relativePath)
		{
			var pathComponents = relativePath.Split(new char[] {Path.DirectorySeparatorChar},
				StringSplitOptions.RemoveEmptyEntries);
			var pathSyntax = "\\" + pathComponents.ComponentsJoinedByString("\\");
			return SkipPaths.Contains(pathSyntax);
		}

		#endregion

	}

	public class ScanRuleAttribute : Attribute
	{
		public ScanRuleTarget Target {get; set;}

		public ScanRuleAttribute(ScanRuleTarget target)
		{
			this.Target = target;
		}
	}

	public enum ScanRuleTarget
	{
		DirectoryInfo,
		FileInfo,
		LineOfText
	}
}