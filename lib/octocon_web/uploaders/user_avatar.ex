defmodule OctoconWeb.Uploaders.UserAvatar do
  @moduledoc false
  use Waffle.Definition

  # Include ecto support (requires package waffle_ecto installed):
  # use Waffle.Ecto.Definition

  @versions [:primary]

  # To add a thumbnail version:
  # @versions [:original, :thumb]

  def acl(:primary, _), do: :public_read

  def validate({file, _}) do
    file_extension = file.file_name |> Path.extname() |> String.downcase()

    if file_extension == "" or
         Enum.member?(~w(.jpg .jpeg .png .webp .heif .heic .tiff), file_extension) do
      :ok
    else
      {:error, "Invalid file type"}
    end
  end

  def transform(:primary, _) do
    &process/2
  end

  def process(:primary, file) do
    with {:ok, original_raw} <- File.read(file.path),
         {:ok, original} <- Image.from_binary(original_raw),
         {:ok, thumbnail} <- Image.thumbnail(original, "500x500", fit: :cover),
         new_path <- Waffle.File.generate_temporary_path(".webp"),
         {:ok, _} <- Image.write(thumbnail, new_path, quality: 90) do
      {:ok, %Waffle.File{file | path: new_path, is_tempfile?: true, file_name: "primary.webp"}}
    else
      _ -> {:error, "Error creating thumbnail"}
    end

    # OctoconWeb.WaffleTransformation.apply(
    #   :convert,
    #   file,
    #   ~w(-strip -thumbnail 500x500^ -gravity center -extent 500x500 -background none -alpha on -quality 90 -limit area 25MB -limit disk 150MB),
    #   :webp
    # )
  end

  # Override the persisted filenames:
  def filename(version, {_file, scope}) do
    "#{version}-#{scope.random_id}"
  end

  # Override the storage directory:
  def storage_dir(_version, {_file, scope}) do
    "uploads/avatars/#{scope.system_id}/self"
  end

  def s3_object_headers(_version, _data) do
    [
      content_type: "image/webp",
      cache_control: "public, max-age=31536000, immutable"
    ]
  end
end
