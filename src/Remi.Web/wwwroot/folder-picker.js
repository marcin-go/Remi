window.remiFolderPicker = {
    relativePaths: (inputId) => {
        const input = document.getElementById(inputId);
        if (!input) {
            throw new Error("The folder picker input was not found.");
        }

        return Array.from(input.files ?? [], file => file.webkitRelativePath || file.name);
    }
};
