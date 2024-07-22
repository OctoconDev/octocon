defmodule OctoconWeb.WaffleTransformation do
  @moduledoc false

  def apply(cmd, file, args, extension \\ nil) do
    new_path =
      if extension,
        do: Waffle.File.generate_temporary_path(extension),
        else: Waffle.File.generate_temporary_path(file)

    args =
      ([file.path] ++ args) ++ ["#{new_path}"]

    program = to_string(cmd)

    ensure_executable_exists!(program)

    result = System.cmd(program, args, stderr_to_stdout: true)

    case result do
      {_, 0} ->
        {:ok, %Waffle.File{file | path: new_path, is_tempfile?: true, file_name: "primary.webp"}}

      {error_message, _exit_code} ->
        {:error, error_message}
    end
  end

  defp ensure_executable_exists!(program) do
    unless System.find_executable(program) do
      raise Waffle.MissingExecutableError, message: program
    end
  end
end
