defmodule Octocon.Utils do
  alias ExAws.S3

  def nuke_existing_avatars!(system_id, folder) do
    bucket = Application.fetch_env!(:waffle, :bucket)
    path = "uploads/avatars/#{system_id}/#{folder}/"

    objects =
      S3.list_objects_v2(bucket, prefix: path)
      |> ExAws.stream!()
      |> Stream.map(fn object -> object.key end)
      |> Enum.to_list()

    S3.delete_all_objects(bucket, objects)
    |> ExAws.request!()
  end

  def nuke_system_avatars!(system_id) do
    bucket = Application.fetch_env!(:waffle, :bucket)
    path = "uploads/avatars/#{system_id}/"

    objects =
      S3.list_objects_v2(bucket, prefix: path)
      |> ExAws.stream!()
      |> Stream.map(fn object -> object.key end)
      |> Enum.to_list()

    S3.delete_all_objects(bucket, objects)
    |> ExAws.request!()
  end
end
