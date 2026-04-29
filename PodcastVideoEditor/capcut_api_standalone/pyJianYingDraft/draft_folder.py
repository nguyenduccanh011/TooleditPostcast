"""Draft folder manager"""

import os
import shutil

from typing import List

from .script_file import Script_file

class Draft_folder:
    """Manage a folder and its collection of drafts"""

    folder_path: str
    """Root path"""

    def __init__(self, folder_path: str):
        """Initialize draft folder manager

        Args:
            folder_path (`str`): Folder containing drafts, typically the CapCut draft save location

        Raises:
            `FileNotFoundError`: Path does not exist
        """
        self.folder_path = folder_path

        if not os.path.exists(self.folder_path):
            raise FileNotFoundError(f"Root folder {self.folder_path} does not exist")

    def list_drafts(self) -> List[str]:
        """List all draft names in the folder

        Note: This function simply lists subfolder names without checking if they conform to draft format
        """
        return [f for f in os.listdir(self.folder_path) if os.path.isdir(os.path.join(self.folder_path, f))]

    def remove(self, draft_name: str) -> None:
        """Remove a draft by name

        Args:
            draft_name (`str`): Draft name, i.e. the folder name

        Raises:
            `FileNotFoundError`: The specified draft does not exist
        """
        draft_path = os.path.join(self.folder_path, draft_name)
        if not os.path.exists(draft_path):
            raise FileNotFoundError(f"Draft folder {draft_name} does not exist")

        shutil.rmtree(draft_path)

    def inspect_material(self, draft_name: str) -> None:
        """Print sticker material metadata of the specified draft

        Args:
            draft_name (`str`): Draft name, i.e. the folder name

        Raises:
            `FileNotFoundError`: The specified draft does not exist
        """
        draft_path = os.path.join(self.folder_path, draft_name)
        if not os.path.exists(draft_path):
            raise FileNotFoundError(f"Draft folder {draft_name} does not exist")

        script_file = self.load_template(draft_name)
        script_file.inspect_material()

    def load_template(self, draft_name: str) -> Script_file:
        """Open a draft as a template for editing

        Args:
            draft_name (`str`): Draft name, i.e. the folder name

        Returns:
            `Script_file`: Draft object opened in template mode

        Raises:
            `FileNotFoundError`: The specified draft does not exist
        """
        draft_path = os.path.join(self.folder_path, draft_name)
        if not os.path.exists(draft_path):
            raise FileNotFoundError(f"Draft folder {draft_name} does not exist")

        return Script_file.load_template(os.path.join(draft_path, "draft_info.json"))

    def duplicate_as_template(self, template_name: str, new_draft_name: str, allow_replace: bool = False) -> Script_file:
        """Duplicate a draft and edit the copy

        Args:
            template_name (`str`): Source draft name
            new_draft_name (`str`): New draft name
            allow_replace (`bool`, optional): Whether to allow overwriting an existing draft with the same name. Default is False.

        Returns:
            `Script_file`: Draft object opened in template mode for the **duplicated** draft

        Raises:
            `FileNotFoundError`: Source draft does not exist
            `FileExistsError`: A draft with `new_draft_name` already exists and overwriting is not allowed.
        """
        template_path = os.path.join(self.folder_path, template_name)
        new_draft_path = os.path.join(self.folder_path, new_draft_name)
        if not os.path.exists(template_path):
            raise FileNotFoundError(f"Template draft {template_name} does not exist")
        if os.path.exists(new_draft_path) and not allow_replace:
            raise FileExistsError(f"Draft {new_draft_name} already exists and overwriting is not allowed")

        # Copy draft folder
        shutil.copytree(template_path, new_draft_path, dirs_exist_ok=allow_replace)

        # Open the draft
        return self.load_template(new_draft_name)
